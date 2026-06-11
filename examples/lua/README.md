# Lua on dotcc — runbook & status

**Status: the full official Lua 5.5 test suite passes.** Lua 5.5.0 compiles
through dotcc (C→.NET 10/C# transpiler), links, and runs — and `testes/all.lua`
(the upstream conformance runner, which dump/undumps every chunk to bytecode and
back) completes with `final OK !!!`. All 28 individual test files pass under the
`_U=true` user-test harness too. For the full feature history see
[`ROADMAP.md`](ROADMAP.md); for the language-feature matrix see
[`../../C-SUPPORT.md`](../../C-SUPPORT.md).

## How to run the suite

```bash
# Build dotcc, then emit+build the standalone interpreter (core+lib+lua.c):
dotnet build DotCC/DotCC.csproj -c Release
cd examples/lua
DLL=../../DotCC/bin/Release/net10.0/dotcc.dll; SRC=lua-src
CORE="lapi lcode lctype ldebug ldo ldump lfunc lgc llex lmem lobject lopcodes lparser lstate lstring ltable ltm lundump lvm lzio"
LIB="lauxlib lbaselib lcorolib ldblib liolib lmathlib loadlib loslib lstrlib ltablib lutf8lib linit"
S=""; for t in $CORE $LIB; do S="$S $SRC/$t.c"; done
dotnet "$DLL" --emit=build -I "$SRC" $S "$SRC/lua.c" -o build_lua
LUA=build_lua/bin/Release/net10.0/build_lua.dll
cd lua-src/testes && dotnet "$LUA" -e '_U=true' all.lua      # → final OK !!!
```

## Mission — DONE

Get the whole Lua core + stdlib to link and run on .NET — `luaL_newstate →
luaL_openlibs → luaL_dostring(...)`, the standalone REPL, AND the full test
suite. Achieved. Remaining stretch: `luac` (standalone bytecode compiler exe;
the dump/undump machinery it relies on already works), readline in the REPL, and
the `T` internal C-API test library (the suite's `testC`-gated tests skip
cleanly without it).

## Fixes that closed the last gaps (2026-06-07 session)

| Area | Fix |
|------|-----|
| C-stack overflow | Run the emitted entry on a 64 MB-stack thread — Lua's `LUAI_MAXCCALLS` guard now trips gracefully instead of a .NET stack overflow (`cstack.lua`). |
| `sizeof(struct) * n` | Stop dropping `*`/`-`/`&` after a struct sizeof (grammar-ambiguity workaround in `SizeofFolder`). |
| Bytecode dump | `SizeofFolder` only folds a sizeof of a PURE type, not an expression containing a typename — fixes `dumpVarint` corruption (`string.dump`/`load`, `calls`/`errors`/`db`). |
| FILE wrong-mode I/O | Reading a write-only / writing a read-only FILE returns EOF/short-count + error indicator instead of throwing (`files.lua`). |
| `/dev/null`, `/dev/full`, `remove()`, file sharing | Unix device shims, `remove()` ENOENT on missing file, permissive `FileShare` (`files.lua`). |
| setjmp routing | Arm each setjmp site with a fresh token so a frame-skipping `longjmp` (`luaD_throwbaselevel`, `coroutine.close` on a running coroutine) reaches the right handler (`coroutine.lua`). |

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

Whole-program link error count: **761 → 0** this run of sessions.

## Current wall — 0 errors

The Lua link compiles cleanly: `0 Error(s)`, 335 warnings (harmless).
All tests pass: 762 unit + 175 functional.

## Next goal — run Lua

The stub `driver.c` just does `return 0`. To actually exercise the emitted
runtime, grow it into a real REPL / script runner:

```
luaL_newstate → luaL_openlibs → luaL_dostring("print('hello')") → lua_close
```

Expected new error classes once runtime paths are exercised:
- Runtime crashes (NullReferenceException, IndexOutOfRangeException) — likely
  from subtle emit bugs that compile fine but misbehave at runtime
- Stdout mismatch vs gcc/oracle — semantic differences in the emitted code
- GC / memory issues — dotcc lowers Lua's custom GC to .NET GC, interactions
  may surface

### Summary of fixes this session (19 → 0, 9 commits):

| Commit | What |
|--------|------|
| `31a8fb6` | Scope field-type map per nesting level (luaL_Buffer) |
| `1b2ec1d` | Rewrite `goto label` → `goto case` for case-start labels |
| `a199e8f` | Propagate `Terminates` through `StmtLabel` |
| `0f8b4d2` | VaArg byte implicit operator |
| `7096fc9` | Void-cast fn-name → nint discard (CS8183 ×2) |
| `c7e031e` | `DecayFnName` on ternary arms (fn-ptr decay) |
| `2a81651` | Hoist cross-case `ret` label out of switch (CS0159 ×3, CS0163 ×2) |
| `6b4bd90` | Compound assign sign-mismatch expansion (CS0034, CS0019) |
| `6196f1d` | CoerceStore cast-expression fix + ternary arm coercion |

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
