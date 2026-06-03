# Lua-on-dotcc — Session Handover

**As of commit `a6b1a9d` (Phase 6v).** This is a resume-from-here snapshot for the
ongoing effort to make **Lua 5.5.0 compile and run under dotcc** (the C→.NET 10/C#
transpiler). For the full feature history see [`ROADMAP.md`](ROADMAP.md); for the
language-feature matrix see [`../../C-SUPPORT.md`](../../C-SUPPORT.md). This file is
the "what's the wall right now and how do I attack it" view.

## Mission

Get the whole Lua core + stdlib to **link and run on .NET** — ultimately
`luaL_newstate → luaL_openlibs → luaL_dostring("print('hello from Lua on .NET')")`
executing via the dotcc-emitted C#. We are in **Phase 6 — Link & run**.

## The working method (do NOT deviate)

1. Re-run the link (`bash link.sh all`) to get the current C# compile-error wall.
2. Pick the largest / most-systemic error family. Look at the *emitted* `build/Program.cs`
   lines and trace each back to the C source shape that produced it.
3. **Reduce to a minimal C reproducer** (`/tmp/foo.c`, emit with
   `dotnet "$DLL" --emit=file /tmp/foo.c`), find the emitter root cause, fix **dotcc**.
4. **Never patch Lua's sources.** If Lua's C is standard, dotcc must handle it. (Lua
   *config* choices — e.g. not defining `__GNUC__` so we get the ANSI VM dispatch —
   are fair game; that's compiler identity, not source edits.)
5. Add a **gcc-oracle-validated functional fixture** (`DotCC.FunctionalTests/Fixtures/<name>/`)
   + **emitter unit tests** (`DotCC.Tests/`). Keep both suites green.
6. One **focused commit per coherent feature**. Commit message trailer:
   `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
7. Update `ROADMAP.md` (phase entry) and `C-SUPPORT.md` (feature row) each landing.

Other standing constraints: no fake keywords in the grammar (bridge C/C# gaps in the
parser/typedef-rewriter); AST recognition over regex on emitted text; type-level
coercion (lowered-type implicit operators) over emitter rewrites where it fits; gate
new dialect-sensitive features with `DialectGate`. Machine is win-arm64; use `python`
(not `python3`) and bash Unix syntax (`/dev/null`, forward slashes).

## How to run

```bash
# Rebuild dotcc after ANY DotCC.Lib change (link uses the built DLL, not a fresh run):
dotnet build DotCC/DotCC.csproj -c Release

# The whole-program link (core + stdlib + driver.c → Roslyn compile):
cd examples/lua && bash link.sh all           # 'all' = 31 TUs; omit for core-only (20)

# Deduped error histogram (MSBuild double-reports, so sort -u is essential):
bash link.sh all 2>&1 \
  | grep -oE 'Program\.cs\([0-9]+,[0-9]+\): error CS[0-9]+' | sort -u \
  | grep -oE 'CS[0-9]+' | sort | uniq -c | sort -rn

# Minimal reproducer:
dotnet "DotCC/bin/Release/net10.0/dotcc.dll" --emit=file /tmp/foo.c

# Tests:
dotnet test DotCC.Tests/DotCC.Tests.csproj -c Release
dotnet test DotCC.FunctionalTests/DotCC.FunctionalTests.csproj -c Release
# gcc oracle (validates a fixture's snapshot against real gcc in WSL):
DOTCC_RUN_GCC_ORACLE=1 dotnet test DotCC.FunctionalTests/... --filter "DisplayName~<fixture>"
```

gcc-oracle in WSL needs the Windows path bridged: `WIN_PWD=$(pwd -W)` then
`MSYS_NO_PATHCONV=1 wsl.exe bash -lc "P=\$(wslpath '$WIN_PWD'); cd \"\$P\" && gcc -std=c17 main.c -o /tmp/x && /tmp/x"`.

## Progress

Whole-program link error count: **761 → 11** this run of sessions.

| Phase | What landed | Errors |
|---|---|---|
| 6i (×4) | C integer-conversion layer | 761 → ~185 |
| 6j–6v | (see earlier handovers) | 185 → 19 |
| 6w | scope field-type map per nesting level (luaL_Buffer); `goto label`→`goto case` for case-start labels; `Terminates` through `StmtLabel`; VaArg byte operator; void-cast fn-name as nint discard; `DecayFnName` on ternary arms | 19 → **11** |

Tests currently green: **762 unit / 175 functional**.

## Current wall — 11 errors

Histogram (deduped):

```
 3 CS0159   2 CS1503   2 CS0163   1 CS0306
 1 CS0266   1 CS0034   1 CS0029
```

Triage notes per family (file:line are into `build/Program.cs` after `link.sh all`):

- **CS0159 (3) — `No such label` for `ret`** (19513, 19546, 19566). These are
  `luaV_execute`'s shared return-handler label `ret` inside `case OP_RETURN`'s brace
  block, targeted by `goto ret` from OTHER cases (OP_TAILCALL etc.). The `l_tforloop`
  / `l_tforcall` labels were fixed in 6w (they're at case starts → `goto case`). `ret`
  is NOT at a case start — it needs the label body HOISTED above/outside the switch.
  **Structural fix needed:** in SwitchBody, detect labels that are cross-case goto
  targets but not at case starts, then extract the label+body to the switch's enclosing
  scope. The label hoisting was prototyped but caused brace corruption in other switches
  (lstrlib's `match` function); needs a more precise extraction that preserves case
  structure.

- **CS1503 (2) / CS0266 (1) / CS0034 (1) / CS0029 (1)** — int↔ulong conversion residue.
  LUAL_BUFFERSIZE is `((int)(16 * sizeof(void*) * sizeof(lua_Number)))` (int cast in
  the macro), but `luaL_prepbuffsize`'s second param is `size_t` (= ulong). The
  `CoerceArg` path should add `(ulong)` but `ArgTypes` may not be populated for the
  argument. Also CS0034 (`ulong += int`) and CS0029 (int→ulong in ternary) are
  residual mixed-type expressions. **Fix:** investigate why `_fnParamTypes` /
  `ArgTypes` isn't flowing for `luaL_prepbuffsize`; add missing coercions. The CS1503
  fn-ptr ternary was fixed in 6w by applying `DecayFnName` to ternary arms.

- **CS0163 (2)** — residual switch fall-through gaps (OP_RETURN, OP_RETURN0). The case
  bodies contain `goto ret;` → `ret:` (same case), with `ret:` body terminating via
  `if/else` where both branches terminate. The `IfElse` visitor doesn't propagate
  `Terminates` (returns a plain string), so the case appears non-terminating. A fix
  was attempted but caused regressions — `TerminatesOf` on if/else branches has gaps
  in the propagation chain. **Fix:** complete the `Terminates` chain through `IfElse`
  and other compound statements.

- **CS0306 (1)** — `UpVal*` as type argument to `Unsafe.AsPointer<T>()` at
  `getupvalref` (2703). C# forbids pointer types as generic type arguments. The global
  `__static_getupvalref_nullup` is `UpVal*`; taking its address produces `UpVal**`.
  `AsPointer<T>` can't handle `T=UpVal*`, and `&staticField` gives CS0212. **Fix:**
  needs a different address-of pattern for pointer-typed globals (maybe `fixed`-based
  accessor, or change the global's declared type to `nint`).

## Pending / deferred tasks

- **#23** Restore the full fn-ptr-table fixture once the fn-ptr-in-aggregate / bare
  fn-ptr **array** (`GlobalArrayFrom<delegate*<…>>` — can't be a generic type arg)
  gaps clear.
- **#24** Harden emitter-qualified references with a `global::` prefix
  (`global::Libc.L`, …) to avoid user-symbol collisions. Deferred per user.
- **Parenthesized simple-MEMBER fn-ptr callee** (`(r.func)(5)` → CS0118): 6l fixed the
  bare-identifier case (`(f)(x)`); the member-access case still reads as a cast.
  Extend the callee-unwrap to a member-access / subscript inner expression.

## Phase 6 end goal

Link all 31 TUs + `driver.c` and **run a Lua script**. `driver.c` is currently a
**stub** (`int main(void){ return 0; }`) — it exists only to shake out whether the
merged core+lib emitted C# compiles & links at all. Once the link is clean, grow it
into `luaL_newstate → luaL_openlibs → luaL_dostring("print('hello from Lua on
.NET')") → lua_close`. After that the remaining work is runtime-semantic (does the
emitted C# actually behave like Lua), which the functional-test harness (Roslyn
in-process + stdout compare) is set up for.
