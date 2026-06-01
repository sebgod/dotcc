# Compiling Lua with dotcc — roadmap

Goal: compile the **Lua 5.5 core + standard library** (`liblua`) with dotcc and
run a real Lua script on .NET, end-to-end. Lua is ~24k lines of dense, portable
C99 — the canonical "serious C program" — so it's both the proof and the
forcing function for the next round of dotcc features.

**Target:** Lua `5.5.0` (github.com/lua/lua, pinned — the current 5.5 release;
there is no 5.5.1 tag yet). Source is fetched, not vendored (MIT, but large) —
see the harness in Phase 0.

**Library target = `liblua`** (Lua's `CORE_O` + `lauxlib` + `LIB_O`), built as a
dotcc `-shared` library, then driven by a small custom `main` through the C API.
The standalone REPL (`lua.c`) and dynamic loader (`loadlib.c`) are out of the
critical path (they need `readline`/`dlfcn`/`windows.h`) and come last as a
stretch.

## File inventory (from Lua's `makefile`)

**CORE_O (20 TUs):** `lapi lcode lctype ldebug ldo ldump lfunc lgc llex lmem
lobject lopcodes lparser lstate lstring ltable ltm lundump lvm lzio`
(`ltests.c` is debug-only — skip.)

**AUX:** `lauxlib`

**LIB_O (11 TUs):** `lbaselib ldblib liolib lmathlib loslib ltablib lstrlib
lutf8lib loadlib lcorolib linit`
(`loadlib.c` → defer: needs `dlfcn.h`/`windows.h`.)

**Standalone (stretch):** `lua.c` (REPL), `luac.c` (bytecode compiler).

## Blockers found empirically (probe: `dotcc --emit=obj -I lua-src lctype.c`)

| # | Blocker | Where | Severity |
|---|---|---|---|
| 1 | **Backslash-newline line continuation** (C translation phase 2) not spliced → lexer throws `unrecognized byte 0x5C` (and as an *unhandled exception*, not a `CompileException`) | `BytesLexer` sees a stray `\`; `luaconf.h:230` (`#define LUA_PATH_DEFAULT \`) + 14 more in that header alone | 🔴 fatal, universal (all multi-line-macro C) |
| 2 | **`stdarg.h` + variadic *functions*** (`va_list`/`va_start`/`va_arg`/`va_end`) | 5 core files: `lobject.c` (`luaO_pushvfstring(…, va_list)`), `lapi.c`, `lauxlib.c`, `lcode.c`, `ldebug.c`. `va_arg` types seen: `size_t,int,char*,void*,l_uacInt,l_uacNumber(double)` | 🔴 fatal; real language + runtime feature |
| 3 | **`locale.h`** missing | 6 files; only real use is `loslib.c` `os_setlocale` (`setlocale`, `LC_*`) + number parsing | 🟠 fatal for those TUs; thin C-locale shim suffices |
| 4 | Deeper VM parse/emit gaps | unknown until #1–#3 land | ❓ |
| 5 | Platform headers (`windows.h`,`unistd.h`,`dlfcn.h`,`readline/*`,`sys/*`,`signal.h`) | only `lua.c` + `loadlib.c` | 🟢 avoidable (defer those TUs) |

Already supported (verified against C-SUPPORT.md): struct/union/typedef/enum,
function pointers, `goto`, bit-fields, `setjmp`/`longjmp` in the `if (setjmp())`
shape — which is exactly Lua's `LUAI_TRY` macro. So the scary parts are covered.

## Phases

Legend: ⬜ not started · 🟦 in progress · ✅ done.

### ✅ Phase 0 — Harness & baseline probe  ← DONE
- `examples/lua/fetch.sh` — shallow-clones Lua `v5.5.0` into a gitignored
  `lua-src/` (don't vendor). Re-runnable; bump `LUA_TAG` to update.
- `examples/lua/probe.sh` — runs `dotcc --emit=obj -I lua-src` over every core
  TU (add `all` for the lib TUs too) and prints a pass/fail table with the first
  error per TU. This is the moving scoreboard for Phases 4–5.

### ✅ Phase 1 — Line continuation (translation phase 2)  ← DONE
Landed: `Compiler.SpliceLineContinuations` splices `\`+LF/CRLF at every source
entry point (TU + headers, user + synthetic); a stray `\` is now a clean
`CompileException`, not an unhandled `LexerException`. Fixture
`line-continuation/` + `LineContinuationTests`. C-SUPPORT updated. `lctype.c`
now lexes clean past `luaconf.h` and stops at the **next** wall (Phase 4):
`parse error at lctype.c:157 unexpected '['` — a **file-scope array
declaration** (`const lu_byte luai_ctype_[UCHAR_MAX+2] = {…}`); dotcc supports
block-scope arrays but not global ones yet.

<details><summary>original plan</summary>
- Splice `\`+newline (and `\`+CRLF) out of source **before** lexing, at every
  point source enters `BytesLexer.FromString` (TU read + the include map, so
  headers are spliced too). Centralize one `SpliceContinuations` helper.
- A genuinely stray `\` (not before a newline) should surface as a clean
  `CompileException`, not an unhandled `LexerException`.
- Decision to record: dotcc-side pre-splice (phase 2 is C-specific — other
  LALR.CC grammars shouldn't splice) vs upstream `BytesLexer`. Leaning dotcc.
- Caveat to document: naive splicing shifts `__LINE__`/error line numbers for
  lines after a continuation; acceptable for v1 (diagnostics only).
- Tests: unit (CompilerTests) + fixture `line-continuation/`. Update C-SUPPORT.
- Exit: the minimal repro and `luaconf.h` lex clean; re-probe to expose the next wall.
</details>

### ✅ Phase 2 — `stdarg.h` + variadic functions  ← DONE
Landed: variadic `...` lowers to `params VaArg[] _va`; `VaArg`/`VaList` runtime
(nested in `Libc`); synthetic `stdarg.h`; `va_start`/`va_arg`/`va_end`/`va_copy`
emitter support (`va_arg` via a dedicated grammar production + terminal, the
others rewritten by name). `VaArg`'s implicit/explicit conversions carry
pointers with no boxing and give C's default promotions for free; `VaList` is a
value type so forwarding + `va_copy` work by struct copy. Fixture `varargs/` +
`VarargTests`; C-SUPPORT updated. Next wall (Phase 4): **`lua.h:157
extern const char lua_ident[];`** — a **file-scope array declaration** (every
TU hits it via `lua.h`); dotcc has block-scope arrays but not global ones.

<details><summary>original plan</summary>

- Grammar: accept trailing `...` in a function **definition/prototype** param
  list (variadic *macros* already work); `va_list` as a type.
- Runtime/lowering (open design — pick in this phase):
  - **A. `object[]` + cursor** (matches dotcc's existing printf builder): a C
    variadic fn → C# `params object[]`; `va_list` → a `(object[],int)` cursor
    struct; `va_arg(ap,T)` → unbox+convert `ap.args[ap.i++]`. AOT-clean, boxes.
  - **B. `__arglist`/`ArgIterator`**: faithful, no boxing, but not AOT-friendly
    and awkward through Roslyn. Likely rejected.
  - Must support **passing a `va_list` to another fn** (`lua_pushfstring` →
    `luaO_pushvfstring`) — so the va_list must be a movable value (favors A).
- Runtime: `DotCC.Libc` gets the `va_list` type + `va_arg<T>` helpers; synthetic
  `stdarg.h` maps the names.
- Tests: fixtures `varargs-sum/`, `varargs-vprintf/` (forward a va_list). C-SUPPORT.
- Exit: a hand-written variadic fn + a v-forwarding fn compile and run correctly.
</details>

### 🟦 Phase 3 — `signal.h` + `locale.h` (missing libc surface)
- ✅ **`signal.h` (header-only)** — `typedef int sig_atomic_t;` + `SIG_DFL`/`SIG_IGN`/
  `SIG_ERR` + the six C-standard signal numbers. This is all the CORE/lib need
  (Lua's `volatile sig_atomic_t trap;`); the `signal()`/`raise()` FUNCTIONS are
  only in `lua.c` (deferred — they'll be backed by .NET's `PosixSignalRegistration`,
  the SIGINT/SIGTERM "terminal" signals, handler-sets-a-volatile-flag idiom). Landed
  with the faithful-`volatile` lowering, so `volatile sig_atomic_t` now both parses
  AND fences. Fixture `sig-atomic/`. **Cleared the `lstate.h:131` wall** — the
  probe's universal failure is now a new one (below).
- ⬜ **`locale.h`** — Synthetic `locale.h` + `DotCC.Libc/LocaleLib.cs`: `setlocale`
  (accepts, returns `"C"`), `struct lconv` + `localeconv` (C-locale values: `.`
  decimal point, etc.), `LC_ALL/COLLATE/CTYPE/MONETARY/NUMERIC/TIME` macros. Exit:
  `loslib.c`'s `os_setlocale` compiles; `localeconv()->decimal_point` works. (Won't
  move the core scoreboard until the array-member wall below clears — it's needed
  by `lobject.c`/`loslib.c` regardless.)

### ⬜ Phase 4k — constant-expression array-member size (`T a[sizeof(X)]`)
The probe's NEW universal wall (18/20 core TUs): a struct array member sized by a
**`sizeof`-expression** — Lua's `lobject.h` `lu_byte extra_[sizeof(void*)]`
(GCObject alignment padding). dotcc's array-member lowering (sized `fixed`/
`[InlineArray]`) currently requires a LITERAL size; `sizeof(void*)` is a constant
*expression* (= 8). Needs a small compile-time evaluator for `sizeof(Type)` (and
`sizeof(Type)`-arithmetic) in array-member bounds — the CType layer already knows
the sizes; it just has to fold the constant. Also surfaced: `lu_byte` (a typedef
for `unsigned char` → `byte`) isn't matched as a primitive fixed-buffer element
(the alias `lu_byte`, not `byte`, reaches the member visitor) — so it takes the
`[InlineArray]` path; resolving the typedef to its primitive would route it to the
plain `fixed byte` buffer. Both are needed to clear this wall. **Next.**

### 🟦 Phase 4 — Core VM TUs (CORE_O)  ← IN PROGRESS (2 / 20 objects)
- Iterate the 20 core TUs via `probe.sh`; fix each parse/emit gap as it surfaces.
  One commit per coherent gap, each with a minimal fixture (dotcc tradition:
  never fix Lua-specifically — reduce to a small reproducer + fixture).
- **Walls cleared** (each a committed dotcc feature + fixture) — the
  **declarator/grammar walls in Lua's core are now exhausted**: `const`/`volatile`
  before a typedef-name/tag (4a), `typedef struct Foo Foo;` tag-vs-typedef
  namespace (4b), file-scope array declarations (4c), parenthesized function-name
  declarator `T (name)(args)` (4d, lua.h's `LUA_API` idiom), `typedef union { … }
  Name;` (4e), named nested aggregate members `struct { … } name;` incl. the
  **tagged** `struct Tag { … } name;` (4f, `StackValue.tbc` / `Node.u`),
  **struct-hack arrays of non-primitive element type** via C# 12 `[InlineArray]`
  + element-pointer access (4g, `UValue uv[1]` / `UpVal *upvals[1]`),
  `typedef enum { … } Name;` (4h, ltm.h's `TMS`), and **multi-declarator members
  + per-declarator pointers** (4i, `struct CallInfo *previous, *next;`).
  **`lctype.c` + `lopcodes.c` emit full objects.** ✅
- **Current wall** (`lstate.h:131`, ~18 remaining TUs) — a **category shift from
  grammar to MISSING LIBC SURFACE**: `volatile l_signalT trap;` →
  `sig_atomic_t trap;`, and dotcc has no synthetic `<signal.h>` so `sig_atomic_t`
  isn't a known type. Plus `<locale.h>` (lobject.c / loslib). The fix is
  synthetic-header / libc-stub work (Phase 3 territory — `signal.h`'s
  `sig_atomic_t`, `locale.h`), NOT more grammar. Likely unblocks several TUs at
  once (4j / Phase 3, next).
- Watch items: `lvm.c` dispatch loop (may use a jump table / labels-as-values —
  GNU `&&label`, which is **out of scope**; Lua has an ANSI fallback `#if`-gated
  on `__GNUC__`, which dotcc doesn't define → we get the portable `switch`).
- Exit: all 20 core TUs emit objects.

### ⬜ Phase 5 — Standard library TUs (lauxlib + LIB_O − loadlib)
- Same loop for `lauxlib` + the 10 lib TUs. `liolib.c`/`loslib.c` lean on stdio
  + a few `os`/`time` calls — extend the synthetic libc as needed.
- Exit: all lib TUs (except `loadlib`) emit objects.

### ⬜ Phase 6 — Link & run (the payoff)
- Link every object as a dotcc `-shared` library (or whole-program with a custom
  main). Write `examples/lua/runlua.c`: `luaL_newstate` → `luaL_openlibs` →
  `luaL_dostring("print('hello from Lua on .NET')")` → close.
- A `loadlib` stub provides `luaL_openlibs` without dynamic loading (drop the
  `package`/`require`-from-.so path).
- Exit: a committed fixture runs a Lua script through dotcc-compiled Lua and
  asserts its stdout. **"Lua runs on .NET via dotcc."**

### ⬜ Phase 7 — Stretch: standalone REPL / `luac`
- Minimal `lua.c` (no readline/signal niceties) and/or `luac.c`.

## Notes / decisions log
- Never patch Lua's sources to suit dotcc — if Lua's C is standard, dotcc should
  handle it; reduce each failure to a fixture and fix dotcc. (Lua-config choices
  like selecting the ANSI VM dispatch via not-defining `__GNUC__` are fair game —
  that's compiler identity, not source edits.)
- Each landed dotcc feature updates `C-SUPPORT.md`, with its fixture.
