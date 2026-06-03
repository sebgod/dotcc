# Lua-on-dotcc — Session Handover

**As of commit `05653dd` (Phase 6t).** This is a resume-from-here snapshot for the
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

Whole-program link error count: **761 → 22** this run of sessions.

| Phase | What landed | Errors |
|---|---|---|
| 6i (×4) | C integer-conversion layer: `Cond.B` per-type overloads, usual arithmetic conversions at operators, store conversions + opt-in `-Wconversion`, call-arg coercion | 761 → ~185 |
| 6j | pointer/fn-ptr typedefs recognised in comma-tuple values (`nint` round-trip) | |
| 6k | C switch fall-through → `goto case` / `goto default` / trailing `break` (CS0163+CS8070) | 185 → 136 |
| 6l | field-chain CType through pointer-typedef/aliased-struct bases + `(void)ptr` / `++ptr` discards + `(f)(args)` bare-name callee unwrap (CS0306 27→0) | 136 → 106 |
| 6m | out-of-range constant integer casts wrapped in `unchecked` (CS0221 17→0) | 106 → 89 |
| 6n | sprintf/snprintf fluent `.Arg(…).Done()` lowering + full `SprintfBuilder` Arg surface + `Arg(void*)` for `%p` (CS1501 15→0; 6 snprintf-`n` CS1503 cleared) | 89 → 72 |
| 6o | drop the `*` on a deref-call through a function pointer `(*fp)(args)` (CS0193 14→0) | 72 → 60 |
| 6p | add `frexp`/`ldexp`/`strcoll`/`ungetc`/`setvbuf`/`tmpnam` libc surface + `luaopen_package` stub in driver.c (CS0103 10→0) | 60 → 50 |
| 6q | address-of a global / static-local via `Unsafe.AsPointer(ref field)` (CS0212 9→0) | 50 → 42 |
| 6r | void-call leading a value-context comma → in-place delegate (CS8210 7→0) | 42 → 35 |
| 6s | `sizeof` yields `size_t` (ulong, unsigned), not `int` — fixes `MAX/sizeof` (CS0034 6→1); + CoerceStore ulong-const-cast bug, malloc bare-int sizeof, char-I/O prototypes | 35 → 24 |
| 6t | C null-pointer constant `0` → `null` at pointer store/return/arg (CoerceStore) | 24 → **22** |

Tests currently green: **759 unit / 173 functional**.

## Current wall — 22 errors

Histogram (deduped):

```
 5 CS0159   4 CS1503   3 CS0266   3 CS0163   2 CS8183
 1 CS0457   1 CS0306   1 CS0034   1 CS0029   1 CS0019
```

Triage notes per family (file:line are into `build/Program.cs` after `link.sh all`):

- **CS0159 (5) — `No such label` for `ret` / `l_tforcall` / `l_tforloop`** (19513,
  19546, 19566, 19715, 19783). These are `luaV_execute`'s VM-dispatch labels. C
  allows `goto` INTO a block; C# does not (only out of / within). The labels live
  inside `switch`-case `{ }` blocks (needed for per-case locals), and the gotos
  target them from OTHER cases — `goto l_tforloop` from `case OP_TFORCALL` jumps
  into `case OP_TFORLOOP`'s block. **Hard, structural.** The targets coincide with
  a case START, so the likely fix is to rewrite `goto <label>` → `goto case <X>`
  when the label is the first thing in case X (dotcc's 6k already emits `goto case`
  for fall-through); `ret` is trickier (jump to shared return handling — may need
  the label hoisted to the switch/loop scope that encloses all the gotos).

- **CS1503 (4) / CS0266 (3)** — conversion residue, now mostly **struct-field
  stores + field-type recording**: `B->size = …` (luaL_Buffer, L20599) and
  `memcpy(…, B->n * sizeof)` (L20505) don't coerce because **luaL_Buffer's field
  types aren't recorded** in `_structFieldTypes` (its `union { LUAI_MAXALIGN; char
  b[…]; } init;` member likely defeats the recorder — a minimal repro of that union
  shape would confirm). `bl->insidetbc = (CBool)…` (L11551) is a `CBool→byte`
  store CoerceStore bails on (CBool isn't an integer type — needs a `(byte)` cast
  via CBool's `→int` operator). `luaL_prepbuffsize(&b, (int)(16*sizeof(void*)))`
  (L22715/41) is `int→size_t` at an arg — check why luaL_prepbuffsize's size_t param
  isn't coercing (possibly a forward-reference: the call emitted before the proto
  registered `_fnParamTypes`? a pre-pass to collect all fn signatures would fix it).

- **CS0163 (3)** — residual switch fall-through 6k missed (a case whose
  terminating-ness wasn't detected). **CS0019 (1)** — a pointer-vs-`0` COMPARISON
  `p == 0` → `int* == int`; extend the 6t null-pointer handling to `==`/`!=` (when
  one operand is a pointer and the other a constant 0, emit `null`). **CS0034 (1)**
  — a residual `ulong/int` not on a sizeof. **CS8183 (2) / CS0457 / CS0306 / CS0029**
  — long-tail singletons, look individually (CS0306 unmasked by 6q: a `&global` now
  compiling feeds a comma-tuple wanting the `nint` round-trip).

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
