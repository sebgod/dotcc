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

### ✅ Phase 3 — `signal.h` + `locale.h` (missing libc surface)
- ✅ **`signal.h` (header-only)** — `typedef int sig_atomic_t;` + `SIG_DFL`/`SIG_IGN`/
  `SIG_ERR` + the six C-standard signal numbers. This is all the CORE/lib need
  (Lua's `volatile sig_atomic_t trap;`); the `signal()`/`raise()` FUNCTIONS are
  only in `lua.c` (deferred — they'll be backed by .NET's `PosixSignalRegistration`,
  the SIGINT/SIGTERM "terminal" signals, handler-sets-a-volatile-flag idiom). Landed
  with the faithful-`volatile` lowering, so `volatile sig_atomic_t` now both parses
  AND fences. Fixture `sig-atomic/`. **Cleared the `lstate.h:131` wall.**
- ✅ **`locale.h`** — Synthetic `locale.h` + `DotCC.Libc/LocaleLib.cs`. dotcc supports
  the **"C" (== "POSIX") locale** (the standard's startup default, the only portable
  one): `setlocale(cat, …)` accepts `NULL`/`""`/`"C"`/`"POSIX"` → `"C"`, else `NULL`
  (category ignored — one locale); `localeconv()` → the "C" `struct lconv`
  (`decimal_point` `"."`, every other string `""`, numerics `CHAR_MAX`); the six
  `LC_*` macros. `struct lconv` is the runtime `Libc.lconv` (declared only there —
  same `struct ID`→bare-tag pattern as `<time.h>`'s `tm`). Unblocks
  `lua_getlocaledecpoint() = localeconv()->decimal_point[0]` (core `lobject.c` +
  `lstrlib.c`/`liolib.c`) and `loslib.c`'s `os_setlocale`. Built as a general C90
  completeness item (it was the only missing C90 baseline header). Fixture
  `locale-c/` (gcc-oracle-validated); unit tests `LibcLocaleTests` (7).
- (Companion fix this batch: `size_t`/`ptrdiff_t` moved from `<stdint.h>` to their
  canonical `<stddef.h>` home; stdint includes stddef so both still resolve.
  Fixture `stddef-types/`.)

### ✅ Phase 4k — constant-expression array-member size + typedef-element resolution
Landed: a struct array-member bound that's an integer CONSTANT EXPRESSION is folded
to the literal a C# `fixed[N]`/`[InlineArray(N)]` needs — a `sizeof` (`sizeof(void*)`
→ 8), an enum constant (`tmname[TM_N]`), and `+ - * / % << >>` arithmetic over them,
seen through parens (`EmitContent.Text.ConstInt`, folded by `ConstOfItem`/`SizeofConst`/
`FoldBinary`; enum values captured in `_enumeratorValues`). Plus a typedef element
resolves to its underlying primitive (`lu_byte`→`unsigned char`→`byte`, via
`_typedefUnderlying`/`ResolveTypedef`) so it takes the `fixed byte` path (C# `fixed`
needs a primitive keyword, not the alias). **Cleared both `extra_[sizeof(void*)]`
(lstate.h) and `tmname[TM_N]` (lstate.h).** Fixture `array-member-constexpr/`; unit
tests `ArrayMemberConstExprTests`.

### 🟦 Phase 4l — multi-dimensional struct array member (✅); diverse next walls
- ✅ **Multi-dimensional struct array member** (`TString *strcache[N][M]`, lstate.h):
  the `Member` grammar now uses `ArrDims` (1-D *and* N-D); a multi-dim member
  flattens to one `fixed T[N*M]` / `[InlineArray(N*M)]` and `s.grid[i][j]` rewrites
  to flat pointer striding (the local multi-dim path). ArrDims also folds each
  dimension (const-expr 4k), so `[sizeof(x)]`/`[ENUM]`/`[2*K]` dims work everywhere.
  Fixture `multidim-member/`; unit tests `MultiDimMemberTests`. **This cleared the
  last shared-header wall** — the core probe jumped **2/20 → 4/20** (`lctype`,
  `lopcodes`, `lmem`, `lzio` now emit objects) and the remaining failures are no
  longer one universal wall but **per-TU gaps**:
  - `unexpected 'TYPE_NAME'` (≈9 TUs, a shared decl form — likely a function-pointer
    typedef or a declarator dotcc doesn't parse yet) — the new most-common wall.
  - `unexpected 'unsigned'` (lcode/llex), `unexpected 'union'` (ldebug),
    `unexpected '['` (lobject/ltm — another array form).
  - `sizeof` of an unsupported expression (ldump); a `setjmp`-in-`if`-without-`else`
    shape (ldo).
- ⬜ **Anonymous file-scope `enum { … };`** — dotcc requires a tag; an untagged
  enum-as-constants definition is common (`lopcodes.h`/`lparser.h`). Needs an
  `enum { … } ;` production emitting the enumerators as constants with no named type.
- **Next:** triage the remaining diverse per-TU walls (4m cleared the `TYPE_NAME`
  cluster, 4n the `[` cluster; what's left is led by the `sizeof`-of-expression gap
  — see the current frontier in Phase 4 below).

### ✅ Phase 4m — MacroExpander argument prescan (the `TYPE_NAME` cluster)
The ≈9-TU `TYPE_NAME` wall was **two causes**: a missing `offsetof` builtin (cleared,
its own commit) and — the larger half, in `lua_tolstring`'s string accessors — a
**MacroExpander rescan bug**. dotcc substituted RAW argument tokens into a macro body
and leaned on the single shared rescan hide set to expand them; per C11 §6.10.3.1 an
argument that isn't a `#`/`##` operand must be **fully macro-expanded in the call-site
context BEFORE substitution**. The reduced trap is `getstr(tsvalue(o))`: the body of
`rawgetshrstr` re-uses `cast`, which the argument `tsvalue(o)` also expands to — so the
outer `cast` painted the hide set and the argument's inner `cast(GCU*,…)` was left
literal, parsing as a type name in expression position. Fix: `Substitute` now pre-expands
each argument once (cached, so a parameter used N times costs one expansion) in a fresh
copy of the call-site hide set, and the regular-substitution path uses the pre-expanded
form; `#`/`##` keep the raw tokens. **Core probe jumped 4/20 → 7/20** (`lstring`, `lvm`,
`ldebug` now emit objects; `lapi`/`lparser`/`lundump` advanced past the macro wall to
new, unrelated walls). Fixture `macro-arg-prescan/` (gcc-oracle-validated = 24); unit
tests `Function_macro_arg_prescan_*` in `CompilerTests.PreprocessorAndLiterals`.

### ✅ Phase 4n — block-scope `static` arrays (the `[` cluster)
Three TUs failed with `unexpected '['` at a local declaration — all the same form:
a **block-scope `static` array** (`static const lu_byte log_2[256] = {…}` in
lobject, `static const lu_byte nextage[]` in lgc, `static const char *const
luaT_eventname[]` in ltm). The grammar's `stmtStaticDecl` only covered the scalar
`static T DeclItemList ;` shape, so the array suffix after the name was rejected.
Added five Stmt productions mirroring the file-scope `globalStatic*` set; a local
static array has the SAME static storage duration as a file-scope one, so each
lowers identically — a pinned global field under a function-mangled name
(`__static_{fn}_{name}`), with in-function uses rewritten via `_fnStatics`. Also
fixed a **pre-existing latent bug** the parse-only probe never caught: an array OF
POINTERS (`const char *const names[]`, both file- and block-scope) emitted
`GlobalArrayFrom<byte*>(new byte*[]{…})` — invalid C# twice over (CS0306/CS0611,
pointer types can't be generic args or array elements). Now stored as a pinned
`nint[]` reinterpreted as `T**`. **Core probe 7/20 → 9/20** (`lgc`, `ltm` emit
objects; `lobject` advanced past the `[` to a struct-member-dimension wall).
Fixture `static-local-array/` (gcc-oracle-validated); unit tests
`StaticLocalArrayTests`.

### ✅ Phase 4o — `sizeof` of an expression through member access / pointer arith
Three TUs failed on `sizeof(expr)` where the operand flowed through a struct
member, pointer arithmetic, or a comma — the CType layer (roadmap item 6) didn't
type those nodes. Extended it on three fronts: (1) **member access** (`s.f` /
`p->f`) now carries the field's recorded CType (new `_structFieldTypes`, drained
per struct like `_structFieldEnums`; a typedef'd pointer field resolves to its
pointee so a deref/subscript peels the `*`), unlocking `sizeof(p->f)`, nested
chains `sizeof(p->in.g)`, `sizeof(*p->f)`, `sizeof(p->f[i])`, and `lstate`'s
`sizeof(*(L->stack.p))`; (2) **additive pointer arithmetic** (`p ± n`) carries the
decayed pointer type (`ArithFold` → `PtrArithType`/`DecayToPointer`), unlocking
`ldump`'s `sizeof((buff + DIBS - n)[0])`; (3) a **value-context comma** carries its
last operand's type (`CommaSeq.LastType`), unlocking `lundump`'s
`sizeof(getstr(o)[0])`. Plus an unfoldable-dimension local array now records its
decayed `elem*` so the element still resolves. **Core probe 9/20 → 12/20** (ldump,
lstate, lundump). Fixture `sizeof-member/` (gcc-oracle-validated); unit tests
`SizeofMemberTests` (incl. a guard that an unsynthesizable `sizeof(a*b)` still
fails loudly). The one remaining `sizeof`-expr gap is non-additive arithmetic.

### ✅ Phase 4p — cast-of-constant array-member bound + 1-D array-member sizeof
`lobject` failed on `char space[BUFVFS]` where `BUFVFS = cast_uint(LUA_IDSIZE +
…)` = `((unsigned int)(219))` — the member-bound const-folder (4k) didn't see
through a cast. Now a cast of an integer constant to an integer type keeps its
folded `ConstInt` (Visit(C.Cast) → IsIntegerCsType), so the bound folds to the
literal a `fixed`/`[InlineArray]` needs. lobject then hit a second wall —
`sizeof(buff->space)` of a 1-D fixed-buffer member gave the decayed pointer size;
now a 1-D array member records its `Arr` CType (in `_pendingFieldTypeMap`), so
`sizeof(s.buf)` is `count * sizeof(element)`. **Core probe 12/20 → 13/20**
(lobject). Fixture `array-member-cast-dim/` (gcc-oracle-validated); unit tests in
`ArrayMemberConstExprTests`.

**~~Deferred~~ → done in 4x — ltable's `char padding[offsetof(Limbox_aux,
follows_pNode)]`:** the array bound is an `offsetof`, which dotcc lowered to a
RUNTIME helper, not a compile-time constant. Folding it needs a compile-time
struct-layout/alignment model. This was deferred here (4p) as its own
capability, then **built in 4x**: a recursive layout walk computes the offset
(`Node` resolves through the union/struct graph to alignment 8 → offsetof 8),
matching the C ABI / .NET blittable layout. See Phase 4x.

### ✅ Phase 4q — function-specifier run before a typedef-name return (`l_sinline`)
`lapi` and `lcode` failed on `l_sinline Table *gettable(...)` — `l_sinline`
expands to `static inline`, and the declaration-specifier sequence `[static]
inline <typedef-name>` couldn't form a Type: a typedef-name is the separate
`Type → TYPE_NAME` production, not a `TypeSpec`, so the spec-list (`inline`)
couldn't absorb it. New `Type → TypeSpecList TYPE_NAME` production
(`typeSpecThenName`) composes them — the TYPE_NAME is the base type, the
spec-list contributes only its `inline`/`_Noreturn` flags — and `typePtr` now
carries those flags forward so a pointer-returning `inline` keeps its
`[MethodImpl(AggressiveInlining)]` (a pre-existing latent drop, also fixed).
Conflict-free (a bare TYPE_NAME still reduces via `Type → TYPE_NAME`; only a
spec-list *followed by* a TYPE_NAME shifts into the new rule). **Core probe
13/20 → 14/20** (lapi). Fixture `inline-typedef-return/` (gcc-oracle-validated);
unit tests in `CompilerTests.Dialect` (`Inline_before_typedef_name_*`,
`Noreturn_before_typedef_name_*`).

### ✅ Phase 4r — block comments closed with `**/` (lexer regex fix)
`lfunc` failed with `unexpected 'ID'` at top-level state — the parser thought
the `newupval` function had ended mid-body. Root cause was the lexer: the
`BLOCK_COMMENT` regex `/\*[^*]*(\*[^/][^*]*)*\*+/` mis-scanned a `**/` close —
it ate the first star as `\*`, the second as `[^/]`, then the `/` fell into
`[^*]*`, so the terminator was missed and the comment ran on to the NEXT `*/`.
Lua closes doc comments with `**/` (`/*\n** …\n**/`), so the comment from one
doc block ran through the function signature + several body lines to the next
inline `/* … */`, silently deleting them. Replaced with the canonical regex
`/\*([^*]|\*+[^*/])*\*+/` (alternation, supported since LALR.CC ≥ 4.0.0) — a
star RUN before `/` is the terminator, never the body. **Core probe 14/20 →
15/20** (lfunc). Pure lexer fix — could only help, no regressions. Fixture
`block-comment-close/` (gcc-oracle-validated); unit tests `BlockCommentTests`.

### ✅ Phase 4s — `setjmp` in an `if` without an `else` (Lua's `LUAI_TRY`)
ldo failed on `#define LUAI_TRY(L,c,f,ud) if (setjmp((c)->b) == 0) ((f)(L, ud))`
— dotcc's setjmp→try/catch rewrite required a matching `else` and rejected the
no-else form. But the missing branch is simply empty: `if (setjmp(env) == 0)
STMT;` ≡ `try { STMT } catch (matching) { }` (swallow the unwind, continue),
and `if (setjmp(env)) STMT;` ≡ `try { } catch (matching) { STMT }`. Extended
`Visit(C.StmtIf)` to apply the same rewrite with an empty block for the absent
side (and `EmitSetjmpRewrite` now skips the unused `__longjmp_value` binding
when the catch is empty). This **advances ldo past the setjmp wall** to a
follow-on `sizeof(*(p.dyd.actvar.arr))` gap (sizeof of a deref through a 3-level
member chain — a CType-layer depth limit, tracked below), so the probe still
reads 15/20, but the setjmp restriction is gone. Fixture `setjmp-no-else/`
(gcc-oracle-validated); unit tests `SetjmpNoElseTests` (3).

### ✅ Phase 4t — `sizeof` through an anonymous-inline-struct member chain
ldo's follow-on wall (after 4s) was `sizeof(*p.dyd.actvar.arr)` — `sizeof` of a
deref through a 3-level member chain where `actvar` is an *anonymous* inline
struct member of `Dyndata` (`struct { Vardesc *arr; int n; int size; } actvar;`).
dotcc lowers such a member to a synthesized nested type (`__NestS0`), but
`EmitNamedNestedAggregate` recorded only the synth type's field *names*, not
their *types* — and didn't record the parent field's type either. So the CType
chain dead-ended at `actvar` and `sizeof(*…arr)` couldn't resolve. Fixed: the
inner fields' CTypes are sliced out of the pending (parent) map onto the synth
type's own `_structFieldTypes` entry (the type analogue of slicing the inner
NAMES off `_pendingFields`), and the parent field is recorded as `CType.Sized
(synthType)`. Now `o.vec.arr` carries its type and the deref/subscript peels to
the element. **Core probe 15/20 → 16/20** (ldo). Fixture
`sizeof-nested-member-chain/` (gcc-oracle-validated); unit tests in
`SizeofMemberTests` (3 added).

### ✅ Phase 4u — `static` struct aggregate init + nested-brace member init
lcode failed on a block-scope `static const expdesc ef = {VKINT, {0}, NO_JUMP,
NO_JUMP}` — two gaps at once. (1) No `static T x = {…}` production: block- and
file-scope static decls only took a scalar `= E`, not a brace aggregate. Added
`stmtStaticStructInit` / `globalStaticStructInit` (disjoint from the scalar
`static Type DeclItemList` on the `{` after `=`, and from the static-array forms
on the absence of `[`), lowering to a once-initialised DotCcGlobals field (block
scope mangled + registered in `_fnStatics`, file scope verbatim). (2) Nested
brace init was rejected ("a nested brace initializer isn't valid here") even
non-static — the `{0}` initializes the union member `u`. Replaced the flat
`Leaves` mapping in `DeclStructInit` with a recursive `StructInitExpr` that, for
a nested brace, looks up the field's struct/union type (via `_structFieldTypes`
— now populated for anonymous-inline members too, phase 4t) and recurses into
`field = new <FieldType> { … }`; for a union that's the first member. **Core
probe 16/20 → 17/20** (lcode). Fixture `static-struct-init/` (gcc-oracle-
validated); unit tests `StaticStructInitTests` (4).

### ✅ Phase 4v — anonymous struct type in a declaration (`struct { … } x;`)
lparser failed on its priority table `static const struct { lu_byte left;
lu_byte right; } priority[] = {…}` — an UNNAMED `struct { … }` used directly as a
declaration's type (not a typedef body or a struct member). New `Type → 'struct'
'{' MemberMark MemberList '}'` (`typeAnonStruct`) synthesizes a name
(`__NestS<N>`), emits the struct decl, records its fields/field-types (sliced off
the pending map like the named-nested case), and returns the synth name as the
Type — so the existing array + aggregate-init productions handle the rest for
free (`__NestS0* priority = …`, each `{l,r}` → `new __NestS0 { left=l, right=r }`;
the `sizeof(t)/sizeof(t[0])` idiom; block-scope vars). Keyed on `struct {` (no
tag) → conflict-free with `struct ID` / `struct ID { … };`, AND — verified — with
`typedef struct { … } Name;` (still lowers to a named `struct Name`, unaffected).
**Core probe 17/20 → 18/20** (lparser). Fixture `anon-struct-decl/` (gcc-oracle-
validated); unit tests `AnonStructDeclTests` (4, incl. the typedef regression guard).

### ✅ Phase 4w — comma operator in a controlling expression + `(void)` discard
llex failed on `while (cast_void(save_and_next(ls)), lisxdigit(ls->current))` —
the comma operator in a `while` controlling expression where a non-last operand
is a VOID side-effect (`save_and_next` = `(save() /void/, next() /assignment/)`,
wrapped in `(void)(…)`). A void operand can't be a C# tuple element, so the
value-tuple lowering can't apply. Fix: (1) grammar — `if`/`while`/`do-while`/
`switch` controlling expr `E` → `Expr` (comma tier). (2) A parenthesized comma
now carries its raw operands forward (`EmitContent.Text.CommaOps`) alongside the
value-tuple, so a discard context can recover them; `CommaOp` + `Paren` both
flatten via `CommaOpsOf` (nested/redundant parens compose). (3) `(void)X` lowers
to a DISCARD — a comma's operands as statements, or `_ = (value)` for a plain
value. (4) The controlling-expr visitors, on a comma condition, lift the
non-last operands into statements and test the last with `Cond.B`
(`while (true) { S; if (!Cond.B(C)) break; BODY }`). Also made `StripOuterParens`
iterate (a macro-parenthesized assignment `(i = i+1)` → `((i = (i+1)))` needs all
redundant layers stripped to be a valid C# statement-expression). **Core probe
18/20 → 19/20** (llex). Fixture `comma-void-control/` (gcc-oracle-validated);
unit tests `CommaControlTests` (5).

### ✅ Phase 4x — compile-time `offsetof` via a struct-layout model
ltable failed on `char padding[offsetof(Limbox_aux, follows_pNode)]` — an
alignment-union trick where the array-member bound is an `offsetof`. dotcc had
only a RUNTIME `offsetof` helper (a real-instance address subtraction), so it
couldn't drive a C# `fixed[N]` literal. Fix: a recursive **struct/union layout
model** (`CSharpEmitter.Layout.cs`) computing (size, align, member offset) with
the standard C-ABI rules — which a .NET blittable struct (`Sequential`, natural
alignment, no `Pack`) / union (`Explicit`, every member at offset 0) ALSO follows
on the same platform, so the folded constant agrees with C# `sizeof`/.NET at
runtime. `offsetof` now folds to a literal (carrying `ConstInt`) when the whole
transitive type graph is modellable — primitives, pointers (incl.
function-pointer typedefs → 8/8, tracked in `_pointerTypedefNames`), nested known
aggregates (struct-vs-union tracked in `_unionTypes`), and 1-D arrays — and
falls back to the runtime helper otherwise (a bit-field's packing is impl-defined
and differs from C, so it bails — correctly). The 64-bit data model matches
dotcc's `<stdint.h>` (pointer/`long` = 8). **Core probe 19/20 → 20/20** (ltable)
— **the Lua core is complete.** Fixture `align-union/` (gcc-oracle-validated:
offsetof 8 / sizeof Limbox 8 / sizeof Node 24, and the Limbox-before-node-array
round-trip); unit tests `OffsetofTests` (7 — fold + helper fallback).

### ✅ Phase 4y — postfix `.`/`->` on a compound base keeps its parens
While validating 4x end-to-end, found a latent precedence bug: a member access
whose base is a COMPOUND expression dropped the protecting parens —
`(p - 1)->v` emitted as `p - 1->v` (parsed `p - (1->v)`, CS0193). The `.`/`->`
visitors over-stripped the base. This is exactly the shape Lua's
`getlastfree(t) = ((cast(Limbox*, (t)->node) - 1)->lastfree)` relies on — latent
because the probe only `--emit=obj`s (no Roslyn compile), but it would break
Phase 6's link-and-run. Fix: `PostfixBase` strips redundant outer parens (clean
output, malloc-promote keying still matches the bare name) but RE-WRAPS a base
with a top-level binary/ternary op, a leading unary, or a leading cast. A bare
identifier / member chain (`a.b.c`) / call / index stays unwrapped. (Subscript
already followed this not-over-stripped rule.) Fixture `ptr-arith-arrow/`
(gcc-oracle-validated); unit tests `PostfixBaseTests` (5).

### 🟦 Phase 4 — Core VM TUs (CORE_O)  ← ✅ COMPLETE (20 / 20 objects)
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
- **Frontier: 20/20 — all core TUs emit objects.** ✅ The last wall (ltable's
  `offsetof`-as-array-bound) fell to the struct-layout model (4x); the
  `(p - 1)->m` precedence shape it depends on was fixed alongside (4y).
- Watch items: `lvm.c` dispatch loop (may use a jump table / labels-as-values —
  GNU `&&label`, which is **out of scope**; Lua has an ANSI fallback `#if`-gated
  on `__GNUC__`, which dotcc doesn't define → we get the portable `switch`).
- Exit: all 20 core TUs emit objects. ✅ **Done.**

### ✅ Phase 5 — Standard library TUs (lauxlib + LIB_O − loadlib)  ← ✅ COMPLETE (31 / 31 objects)
The library TUs came in almost free off the core work — `dotcc --emit=obj` over
`lauxlib` + the 10 lib TUs started at **29/31**, with only three small gaps, each
a general-C-completeness item (not Lua-specific). All cleared, 31/31:
- ✅ **Phase 5a — `<stdio.h>` implementation-defined limit macros.** `lauxlib`
  failed on `char buff[BUFSIZ];` (a struct member) — `BUFSIZ` wasn't defined in
  the synthetic `<stdio.h>`. Added the standard limit + buffering-mode macros
  (`BUFSIZ`/`FILENAME_MAX`/`FOPEN_MAX`/`TMP_MAX`/`L_tmpnam`/`_IOFBF`/`_IOLBF`/
  `_IONBF`, C99 7.21.1) with values satisfying the standard minima; `BUFSIZ` now
  folds as a constant array bound. **Cleared `lauxlib`.** Fixture `stdio-limits/`;
  unit tests `StdioLimitsTests`.
- ✅ **Phase 5b — anonymous `union` type in a declaration.** `lstrlib` failed on
  its native-endianness probe `static const union { int dummy; char little; }
  nativeendian = {1};` — an unnamed `union { … }` used directly as a declaration's
  type (the union counterpart of the anon-struct-type, Phase 4v). New `Type →
  'union' '{' MemberMark MemberList '}'` (`typeAnonUnion`) synthesizes a name,
  emits an explicit-layout struct, returns the synth name as the Type. **Advanced
  `lstrlib`** past line 1413. Fixture `anon-union-decl/`; unit tests
  `AnonUnionDeclTests`.
- ✅ **Phase 5c — block-scope (local) aggregate type definitions.** `lstrlib`
  then failed on `struct cD { char c; union { LUAI_MAXALIGN; } u; };` — a
  `struct`/`union`/`enum` defined *inside a function body* as a statement. A type
  has no storage, so each new `Stmt → 'struct'/'union'/'enum' ID '{' … '}' ';'`
  production (`stmtStructDef`/`stmtUnionDef`/`stmtEnumDef` + the C23 `enum : T`
  variant) hoists the type into the top-level section (deduped by tag) and emits
  nothing at the statement — delegating to the same emit as the `Fn`-level defs.
  **Cleared `lstrlib`** — probe 31/31. Fixture `local-type-def/`; unit tests
  `LocalTypeDefTests`.
- Exit: all lib TUs (except `loadlib`) emit objects. ✅ **Done — 31/31.**

### 🟦 Phase 6 — Link & run (the payoff)
- Link every object as a dotcc `-shared` library (or whole-program with a custom
  main). Write `examples/lua/driver.c`: `luaL_newstate` → `luaL_openlibs` →
  `luaL_dostring("print('hello from Lua on .NET')")` → close.
- A `loadlib` stub provides `luaL_openlibs` without dynamic loading (drop the
  `package`/`require`-from-.so path).
- Exit: a committed fixture runs a Lua script through dotcc-compiled Lua and
  asserts its stdout. **"Lua runs on .NET via dotcc."**

**Harness:** `examples/lua/link.sh` whole-program-compiles all TUs + `driver.c`
(`--emit=build` → Roslyn). Unlike `probe.sh` (`--emit=obj`, parse+emit only),
this is the first time the MERGED emitted C# is actually COMPILED — surfacing a
new class of latent bugs the probe never could.

- 🟦 **Phase 6a — compile-shakeout emitter fixes.** The first whole-program build
  (31 TUs + stub `main`) produced 128 C# errors in three families, each a genuine
  latent emitter bug:
  - ✅ **Void-typed ternary as a statement** (132 → 14 errors): Lua's GC
    write-barriers `(cond ? luaC_barrier_(…) : cast_void(0));` — a `?:` whose
    branches are void (a `(void)X` cast or a void call) can't be a C# expression.
    Now lowered to an `if`/`else` statement (`EmitContent.VoidCond`), recursing for
    nested void ternaries and propagating through redundant parens. Fixture
    `void-ternary-stmt/`.
  - ✅ **Braceless control-flow body that's a multi-statement comma** (cleared the
    `'else' cannot start a statement` family): Lua's `luaL_addchar(B,c)` =
    `((void)(…), (…))` as a braceless `if`/`else` body — the comma expands to
    several statements, so it's block-wrapped to stay one statement. Fixture
    `braceless-comma-body/`.
  - ✅ **`setjmp` try-body bracing** (cleared the `{ expected` pair): `LUAI_TRY` =
    `if (setjmp((c)->b) == 0) (f)(L, ud);` — the try BODY must be a block
    (`try stmt;` is invalid C#). Fixture `setjmp-try-body/`.
  - ✅ **Postfix `++`/`--` on a deref** (found via the void-ternary fixture's
    helper): `(*p)++` must keep its parens — `*p++` is `*(p++)` (wrong + CS0201).
    The postfix-`++` analogue of Phase 4y's `PostfixBase`. Fixture
    `deref-incr-decr/`.
- ✅ **Phase 6b — value-context void-comma** (cleared the 14): Lua's
  `luaM_newvectorchecked` = `(luaM_checksize(…), luaM_newvector(…))` — a comma
  whose leading operand is a void guard ternary, in VALUE position (assigned). A
  void value can't be a C# tuple element, so `EmitContent.SeqExpr` carries the
  leading operand(s) as statements + the comma's value, hoisted at the
  statement-level sink (assignment / `return` / expression-statement) as
  `{ leadingStmts; sink(value); }`. Propagates through Paren/Cast; a non-hoisting
  value position (function arg, …) fails loudly. Fixture `comma-void-value/`.
  **The whole 31-TU program now compiles past the statement/expression layer** —
  remaining errors are missing typedef resolutions (`size_t`/`lu_byte`/… as bare
  type names) + a `void` parameter, the next wall (6c).
- ✅ **Phase 6c — typedef resolution inside `using` alias bodies** (cleared the
  CS0246/CS1536): a C# `using X = Y;` resolves Y IGNORING all other using-aliases,
  so a scalar typedef-name in an alias body (an alias-of-alias `typedef intptr_t
  lua_KContext;`, or a function-pointer typedef's `delegate*<…, size_t*, …>`) was
  CS0246. `ResolveTypedefInType` now resolves scalar typedef-names there to their
  primitive (`size_t`→`ulong`, `intptr_t`→`long`, `lu_byte`→`byte`). Also: a
  `(void)` fn-ptr parameter list now means NO parameters (`delegate*<void>`, not
  `delegate*<void, void>` = CS1536). Fixture `typedef-alias-resolve/`.
- ✅ **Phase 6d — function emission model: top-level locals → class methods**
  (cleared ~1200 errors: the whole CS8422 (898) + CS8801 (306) local-function
  family; total ~5000 → ~3280). dotcc used to emit each C function as a **top-level
  local function** in the implicit `Main` — fine for small programs, but a
  top-level local function can't be addressed (`&fn`), stored in a function-pointer
  table (Lua's `luaL_Reg`), or referenced from a file-scope initializer / several
  C# contexts (CS8801/CS8422/CS8787). Now user functions are **`internal static`
  methods of a `DotCcProgram` class** (globals stay in `DotCcGlobals`); `using
  static DotCcProgram;` surfaces them by bare name everywhere, so the entry's
  `main(...)` call, inter-function calls, and a file-scope `&fn` all resolve — `&fn`
  across the class boundary works (using-static surfaces the method group, verified).
  Behaviorally transparent: all examples (calc/factorial/hello) + 154 functional +
  684 unit pass. Fixture `fnptr-table/`; unit tests `EmissionModelTests`.
- ✅ **Phase 6e — pointer-valued comma expressions** (cleared CS0306 1006 → 74;
  total ~3280 → ~2350). `(a, b)` in value position lowers to a C# `ValueTuple`
  `(a, b).Item2`, but a **pointer** operand can't be a tuple type argument (CS0306)
  — Lua's `check_exp(c, e)` = `(lua_assert(c), e)` with a pointer `e`, nested in
  value position (inside a cast / member access / `&`) so it can't be statement-
  hoisted like 6b. Now `CommaSeq` carries per-operand `CType`s, and the value-tuple
  casts a pointer operand to `nint` (pointer-width, round-trips) and casts the
  `.ItemN` back to the pointer type when the value is a pointer. Fixture
  `comma-pointer-value/`. (Residual 74 CS0306 = bare fn-ptr arrays /
  untyped-pointer operands — fold into the items below.)
- ✅ **Phase 6f — string-literal helper collision with a user `L`** (cleared
  CS0149 840 → 0; total ~2350 → ~1520). CS0149 "method name expected" wasn't the
  function-decay cluster as guessed — it was the string-literal helper `L(...)`
  being SHADOWED by Lua's ubiquitous `lua_State *L` parameter, so `L("…")` tried
  to "call" the variable `L`. The four string-literal emit points now emit the
  helper QUALIFIED as `Libc.L(...)`, which a user `L` can't shadow. Fixture
  `string-literal-L-collision/`. (A user type/var named `Libc` could still shadow
  `Libc.L` — the bulletproof fix is `global::Libc.L`; tracked as a broader
  `global::` hardening sweep, deferred.)
- ✅ **Phase 6g — function-name decay in aggregate initializers + assignments**
  (cleared CS8787 312 → 0). A bare function name used as a value decays to a
  pointer-to-function (C §6.3.2.1); C# requires the explicit `&`. dotcc already
  added it for call args and scalar fn-ptr decl-inits — extended it to the four
  remaining value positions: struct-array element initializers (Lua's `luaL_Reg`
  `{ "name", cfunc }` tables), C99 designated `.field =`, compound literals, and
  plain assignments to a fn-ptr lvalue. `DecayFnName` also now sees through outer
  parens (`(luaB_next)` → `&luaB_next`) while preserving `@`-escaping. Fixture
  `fnptr-aggregate-init/`. (Latent gap surfaced: a direct call through a
  parenthesized *simple-member* fn-ptr callee, `(r.func)(5)`, reads as a C# cast
  → CS0118; rare in Lua — 2 sites — because a subscripted base `tbl[i].func(…)`
  disqualifies the cast reading. The fixture reads the field into a local fn-ptr
  first; the call-through case is below.)
- ✅ **Phase 6h — enum case labels / switch subject reconcile to int** (cleared
  the CS0266 `RESERVED`→`int` chunk, 96 → 0; CS0266 210 → 114). A C `switch` is
  int-semantic — the controlling expression is integer-promoted and case labels
  are converted to that type — so Lua's lexer switches a plain-`int` token field
  against `enum RESERVED` member labels (`case TK_NAME:`). dotcc lowers enums to
  real C# enums, which reject both `switch(int){ case Enum.X }` and
  `switch(Enum){ case (int)… }`. Fix: decay an enum-typed switch subject AND
  enumerator case labels to `(int)` (the existing `IntDecay` enum-sink helper) —
  uniform int = pure C semantics, and a switch ON an enum keeps working. Fixture
  `enum-switch-int/`.
- ✅ **Phase 6i — C integer-conversion layer** (509 → 305). dotcc maps C's integer
  types onto C# (`size_t`/`lua_Unsigned` → `ulong`, `lu_byte` → `byte`, …); C#
  performs most of C's conversions implicitly but diverges where this layer steps
  in. Landed in three pieces:
  - **pt1 — `Cond.B` overload per numeric type** (CS0121 152 → 0). A controlling
    expression of any integer type wraps in `Cond.B(...)`; the overload set was
    only bool/int/double/void*/CBool, so a `byte`/`uint`/`long`/`ulong`/… argument
    was ambiguous between a built-in numeric overload and the user-defined `CBool`
    conversion. Added an exact overload per lowered numeric type (an exact match
    beats the user-defined path). Fixture `cond-int-types/`.
  - **pt2 — usual arithmetic conversions at binary operators** (CS0034 180 → 12;
    CS0019 shifts 18 → 6). C# refuses to unify a 64-bit unsigned (`ulong`/`nuint`)
    with a signed integer (CS0034), and widens `uint op int` to `long` where C
    keeps `unsigned int`. dotcc computes C's common type (`ReconcileInt` /
    `IntCommonType`) and, when it's unsigned, casts the signed operand to it
    (C's wraparound conversion) — across `+ - * / %`, `& | ^`, and the relational /
    equality ops; a shift casts its wide/unsigned COUNT to `int`. The result is
    tagged with the common type so it propagates up nested expressions
    (`(size_t)a + (uint)b * sizeof(T)`), through parens and ternary arms, and into
    Cond.B / stores. Fixture `usual-arith-conv/`. *Residual 12 CS0034*: a
    parenthesized `sizeof` loses its int-ness through the paren (Paren drops the
    SizeofType marker); the principled fix is to type `sizeof` as `size_t` (wide
    ripple — deferred).
  - **pt3 — store conversions + `-Wconversion`** (CS0266 112 → 38). C allows an
    implicit narrowing / sign-incompatible conversion at a store (init / assignment
    / return); C# requires an explicit cast. dotcc coerces the value to the target
    type (`CoerceStore`), inserting `(target)(value)` exactly when C# wouldn't
    convert implicitly — an out-of-range CONSTANT gets `unchecked(...)` (else
    CS0221), a constant that FITS gets nothing (C#'s implicit constant conversion,
    like C). New opt-in flag **`-Wconversion`** (off by default, like gcc/clang)
    warns at each width-NARROWING store via a `ConversionGate` collector flushed to
    stderr (the same channel as the dialect gate). Fixture `narrowing-store/`.
  - **pt4 — conversions at call arguments** (CS1503 210 → 14; total 305 → 195).
    dotcc records each function's parameter types (`_fnParamTypes`, populated in
    `StartFn` from the staged params) and coerces every argument to its parameter
    (`CoerceArg` — the call-site twin of the store reconcile: enum↔int, then the
    integer narrowing/sign cast). Only fires for callees with recorded fixed
    params (user functions + the synthetic-header libc prototypes, which match the
    emitted signatures); a variadic call's extra args and calls through fn-ptr
    locals keep the plain decay. A `sizeof` argument is treated as `int` so it
    coerces into a `size_t` parameter. **Found+fixed a latent `CsImplicitInt` bug**
    here — the unsigned-type set omitted `ulong`, so `int→ulong` was wrongly judged
    an implicit widening and silently *not* cast; the fix cleared the `int→ulong`
    stores (6i·3) AND args at once. Fixture `call-arg-conv/`.
- ✅ **Phase 6j — pointer / fn-ptr TYPEDEF + fn-ptr type in comma-tuples**
  (CS0306 74 → 54; total 195 → 185). A comma operator in value position lowers to
  a C# tuple `(a, b).ItemN`; a pointer operand can't be a `ValueTuple` type
  argument (CS0306), so 6e round-tripped it through `nint`. But the detector only
  matched a literal `T*` — it missed a pointer TYPEDEF (`StkId` → `StackValue*`)
  and a function-pointer type/typedef (`delegate*<…>` / `lua_CFunction`), neither
  of which ends in `*`. New `IsPointerCsType` (resolves the typedef chain +
  consults `_pointerTypedefNames` + recognizes `delegate*<`) fixes both; the
  nint-cast and cast-back use the original type name (round-trips via the using
  alias). Required making `T` / `IntDecay` / `CondOf` / `CommaTupleText` instance
  (they need `ResolveTypedef`). Also completed fn-name decay for a **typedef'd
  fn-ptr decl-init** (`CFunc cf = dbl;` → `&dbl`; the `(*fp)()` declarator already
  decayed). Fixture `comma-ptr-typedef/`. *Residual 54 CS0306*: comma-tuple
  operands whose CType isn't synthesized (field chains like `(&v->val)->value_.f`
  — a union member's fn-ptr type isn't propagated; same class as the enum-field
  gap), plus the `(f)(L,ud)` fn-ptr call-through. The bare-fn-ptr-ARRAY case
  (`GlobalArrayFrom<delegate*<…>>`, task #23) is separate — not in this residual.
- ✅ **Phase 6k — switch fall-through (`goto case` / trailing `break`)** (CS0163
  50 → 0, CS8070 48 → 0; total 185 → 136). C lets a case section fall into the
  next when it doesn't end in `break`/`return`/…; C# forbids implicit fall-through
  (CS0163) and forbids the final case falling out (CS8070). dotcc now carries the
  block's statement list as a **structured `StmtSeq`** (pieces with control-flow
  facts — is-this-a-`case`/`default`-label + does-it-terminate) instead of joining
  to text immediately; `StmtSwitch` groups the pieces into case sections and, for
  a section whose last piece doesn't terminate, inserts the explicit jump C
  performs — `goto case <next>;` / `goto default;`, or a trailing `break;` on the
  final section. The right-recursive `StmtList` reduces tail-first, so the whole
  body (every case below) is known when the switch reduces — no separate pass. The
  jump statements / labels / blocks tag `Terminates`; stacked labels and
  already-terminating sections are left alone. Fixture `switch-fallthrough/`.
- ✅ **Phase 6l — field-chain CType + fn-ptr call-through (CS0306 27 → 0;
  total 136 → 106).** Four root-cause fixes feeding the comma-tuple's pointer
  detection, all converging on "synthesize the operand's type so the tuple can
  `nint`-cast a pointer":
  - **Pointer-typedef field base.** The Field* helpers (`FieldCType` /
    `FieldEnum` / `FieldAtomic` / `FieldVolatile[Pointee]` / `FieldInlineArr` /
    `FieldMultiDim` / `PromotedSynth`) keyed the struct-field tables off
    `s.CsType.TrimEnd('*')` — which misses a base that's a *pointer typedef*
    (`StkId` → `StackValue*`) or a pointer to an *aliased* struct (`LStream*` →
    `luaL_Stream`). A shared `StructKeyOf` now alternately peels `*` and resolves
    typedefs to the underlying aggregate, so `((&(func->val))->value_).f` finds
    `f`'s `lua_CFunction` type and the tuple `nint`-casts it.
  - **`(void)X` discard carries X's CType.** A `(void)ptr` comma operand
    (Lua's `check_exp` lead) was untyped, so a pointer discard wasn't
    `nint`-cast. The void-cast Text now carries `Ty: TyOf(operand)`.
  - **`++p` / `p++` carry the operand's pointer type**, so a discarded pointer
    increment (`(void)(++mode)`) `nint`-casts.
  - **Parenthesised bare-name callee unwrap.** Lua's `LUAI_TRY` spells a direct
    fn-ptr call as `((f)(L, ud))`; C# reads `(f)(…)` as a cast (→ tuple, CS0306).
    `Visit(C.Call)` strips the redundant parens around a simple-identifier callee
    → `f(L, ud)` (which also routes it through the `_localNames` shadow path).

    Fixture `fnptr-field-comma/`; unit tests `FieldChainCommaTests`.
- ✅ **Phase 6m — out-of-range constant casts wrapped in `unchecked` (CS0221
  17 → 0; total 106 → 89).** C truncates an out-of-range integer CONSTANT cast
  (mod 2^width); C# rejects it (CS0221) unless wrapped in `unchecked(...)`. dotcc
  wraps an integer cast whose operand is a constant expression that isn't provably
  in range — Lua's pervasive `cast_byte(~mask)` (a bit-clear; `~(1<<6)` folds to
  −65), `(size_t)-1`, and `cast_int(MAX_SIZET/sizeof(t))`. Two parts:
  - **Value fold for `~` / unary `-` / `& | ^` / `<< >>`** — these carry their
    folded `ConstInt` now (only when the result stays a signed ≤32-bit `int`, so
    the int fold matches C — a wider/unsigned result like `~(size_t)0` would
    mis-fold), so `Visit(C.Cast)` sees the value and wraps when it's out of range
    (resolving the target through typedefs, so `lu_byte`/`size_t` casts count).
  - **`ConstExpr` flag** on `EmitContent.Text` — true when an expression is a C
    compile-time constant *independent of whether the value folds*. dotcc can't
    fold a uint-modular shift (`(~0u)<<3`) or a ulong-wide divide
    (`MAX_SIZET/sizeof`) into a 32-bit int, but they're still constants C# rejects
    out-of-range — so the flag (propagated through literals, enumerators, sizeof,
    and every arithmetic / bitwise / shift / unary / cast / paren node) gates the
    wrap. A constant that *provably* fits stays bare (`(uint)(219)`), as does a
    runtime cast (it truncates silently). Fixture `const-cast-unchecked/`; unit
    tests `ConstCastUncheckedTests`.
- ✅ **Phase 6n — `sprintf` / `snprintf` fluent lowering (CS1501 15 → 0; total
  89 → 72).** `printf`/`fprintf` were lowered to the fluent `.Arg(…).Done()`
  builder, but `sprintf`/`snprintf` weren't — so a variadic call overran the
  2-/3-arg `SprintfBuilder` factory (CS1501). Three parts:
  - **Emitter:** `Visit(C.Call)` now lowers `sprintf(dst, fmt, …)` →
    `sprintf(dst, fmt).Arg(…).Done()` and `snprintf(dst, n, fmt, …)` likewise,
    mirroring the `fprintf` case. `.Done()` is unconditional (a `SprintfBuilder`
    left without it never copies). `snprintf`'s `int n` bound takes the
    store-conversion cast for an unsigned/wider C operand (Lua's `sz - n`,
    unsigned `maxitem`) — cleared the 6 `uint → int` CS1503 tails.
  - **Runtime:** `SprintfBuilder` now mirrors `PrintfBuilder`'s full `Arg`
    surface (added `long`/`uint`/`ulong`/`bool`/`Float128`); a missing overload
    was a latent miscompile (a `long` bound to `Arg(float)`), not a compile error.
  - **Both builders gained `Arg(void*)`** — `%p` with a `void*` / typed `T*`
    couldn't bind (`void*` → `byte*` isn't implicit; `T*` → `void*` is). One
    overload covers every typed pointer. Fixture `sprintf-snprintf/`; unit tests
    `SprintfLoweringTests` + `LibcTests` (long/void* overloads).
- ✅ **Phase 6o — deref-call through a function pointer (CS0193 14 → 0; total
  72 → 60).** Lua calls fn pointers pervasively via the deref form `(*fp)(args)`
  — `(*g->frealloc)(…)`, `(*ci->u.c.k)(…)`, `(*cf)(L)`. In C, `*fp` on a function
  pointer is a no-op (the function decays straight back to the pointer), so it's
  identical to `fp(args)`; but C# calls function pointers directly and rejects
  `*fp` (CS0193). `Visit(C.Deref)` now drops the deref when the operand's CType
  is a function pointer — a bare `delegate*<…>` or a fn-ptr typedef (matched via
  `_pointerTypedefNames`, since those typedefs are `using` aliases, not in
  `_typedefUnderlying`). A DATA pointer's `*p` stays a real dereference. Fixture
  `fnptr-deref-call/` (local / param / struct-field); unit tests
  `FnPtrDerefCallTests` (incl. a data-pointer-deref guard). The cleared lines now
  reach arg-checking, surfacing 2 fn-ptr call-arg conversions in the CS1503 tail.
- ✅ **Phase 6p — missing libc names (CS0103 10 → 0; total 60 → 50).** Six libc
  functions Lua needs were neither declared in the synthetic headers nor
  implemented in `DotCC.Libc`, so their calls emitted verbatim and failed at C#
  compile: `frexp`/`ldexp` (mantissa/exponent, via `Math.ILogB`/`ScaleB` + the
  `MathF` float overloads), `strcoll` (→ `strcmp`, since dotcc runs the "C"
  locale), `ungetc` (one-byte pushback honored by `ReadByteFrom`; a `FileSlot.
  Pushback` field), and `setvbuf` (validated no-op — the BCL owns buffering).
  Each got a synthetic-header prototype (so call-args coerce) + a runtime impl.
  `tmpnam` too (full OS temp path; `L_tmpnam` bumped 20 → 260 to hold it). The
  last CS0103, `luaopen_package`, is the deliberately-excluded loadlib TU (dynamic
  C-module loading) — stubbed in `driver.c` (our harness) to push an empty
  `package` table. Fixture `libc-frexp-ldexp-strcoll-ungetc/`; unit tests
  `LibcAddedSurfaceTests`.
- ✅ **Phase 6q — address-of a global / static-local (CS0212 9 → 0; total
  50 → 42).** Lua takes the address of file-scope globals and function-static
  sentinels — `&absentkey`, `&dummynode_`, `&<static-local>`. dotcc lowers these
  to C# `static` fields, which the language classifies as MOVEABLE variables, so a
  bare `&field` is CS0212 ("address of unfixed expression") — even though a C
  global's address is a stable constant and a `fixed` block couldn't span the
  address escaping the function (`return &absentkey;`). Since dotcc's globals are
  unmanaged value types stored in non-relocatable static storage, the address IS
  stable; `Visit(C.AddrOf)` now hands it back via
  `(T*)Unsafe.AsPointer(ref field)`. `Visit(C.Var)` tags the operand (a new
  `EmitContent.Text.AddrFixedType`) only when the name resolves to a global — a
  shadowing LOCAL is a fixed variable, so its `&` stays the plain form. Fixture
  `addr-of-global/`; unit tests `AddrOfGlobalTests` (incl. a plain-local guard).
  Exposed 1 CS0306 behind a previously-CS0212 line (joins the tail).
- ✅ **Phase 6r — void-call leading a value-context comma (CS8210 7 → 0; total
  42 → 35).** A comma `(voidcall, value)` — Lua's `(checktab(…), luaL_len(…))`,
  `(luaO_tostring(L,o), 1)` — has a `void` leading operand, which can't be a C#
  tuple element (CS8210). The existing void-handling only recognised a void guard
  *ternary*, not a void *call*. The fix is surgical at the value-tuple build
  (`Visit(C.Paren)`): when a non-last operand is void-typed, the VALUE form becomes
  an immediately-invoked delegate `(((Func<int>)(() => { leading… return 0; }))(),
  value).Item2` (a fixed-`int` delegate runs the side effects; a tuple picks the
  value so C# infers its type — no value-type synthesis needed; a pointer value
  round-trips through `nint`). The delegate preserves evaluation order **in place**,
  so it's correct deep inside a short-circuiting `&&`/`||` where hoisting can't
  reach. Crucially `CommaOps` still carries the raw operands, so discard /
  statement / controlling / `(void)`-cast contexts split to plain statements as
  before (no delegate in the hot lexer path — Lua's `(save(…), next(…))`). The
  `T(SeqExpr)` value-render likewise delegates now (was a hard error). **Known
  cost:** a delegate (+ closure) allocation per evaluation at the non-hoistable
  sites — see the C-SUPPORT comma row; a future optimization could hoist more
  positions or emit a local function. Fixture `comma-void-call/` (incl. a
  short-circuit check); unit tests `CommaVoidCallTests`.
- ✅ **Phase 6s — `sizeof` yields `size_t` (unsigned), not `int` (CS0034 6 → 1;
  total 35 → 24).** C's `sizeof` is `size_t` — an UNSIGNED type, 64-bit on every
  64-bit target (gcc/MSVC). dotcc had been tracking C#'s `sizeof` operator type
  (`int`), so `MAX_SIZET / sizeof(T)` emitted `ulong / int` → CS0034 (no common
  type in C#). Now `sizeof` lowers to `(ulong)sizeof(T)` (dotcc's `size_t` =
  `unsigned long` = `ulong`, matching `offsetof`), tagged unsigned — so the
  arithmetic reconcile sees `ulong / ulong`. Ripples handled: (a) `CoerceStore`
  had a latent bug — it skipped the cast for a *fitting constant* assuming C#'s
  implicit constant conversion, but that exists only FROM `int`, not from
  `ulong`/`long` — so `int x = sizeof(T)` now correctly emits `(int)((ulong)
  sizeof(T))`; the `unchecked` wrap is reserved for genuinely out-of-range
  constants. (b) `malloc(sizeof(S))` emits the bare `sizeof(S)` (int) directly,
  since `malloc` takes `int`. (c) the char-I/O libc functions (`fgetc`/`getc`/
  `fgets`/`fputc`/`putc`/`fputs`) were never declared in synthetic `stdio.h` —
  added their prototypes so dotcc knows the `int n` param and coerces a `size_t`
  sizeof arg (Lua's `fgets(buf, sizeof(buf), fp)`). Fixture `sizeof-unsigned/`;
  emit-shape unit tests across `CompilerTests.Dialect` / `SizeofMemberTests` /
  `UsualArithConvTests` / `CallArgConversionTests` / `AnonStructDeclTests` updated.
- 🧱 **Phase 6t+ — the remaining deep walls** (~24 errors):
  - **CS1503 (~5), CS0159 (~5), CS0266 (~4), CS0163 (~3)** — a diverse long tail.
  - **Call through a parenthesized simple-member fn-ptr callee (CS0118).** `(r.func)(5)`
    reads as a cast in C#; strip the redundant callee parens when the inner
    expression is a member access / subscript (the bare-identifier case landed in 6l).
  - The fn-ptr-ARRAY `GlobalArrayFrom` (task #23) extension.

### ⬜ Phase 7 — Stretch: standalone REPL / `luac`
- Minimal `lua.c` (no readline/signal niceties) and/or `luac.c`.

## Notes / decisions log
- Never patch Lua's sources to suit dotcc — if Lua's C is standard, dotcc should
  handle it; reduce each failure to a fixture and fix dotcc. (Lua-config choices
  like selecting the ANSI VM dispatch via not-defining `__GNUC__` are fair game —
  that's compiler identity, not source edits.)
- Each landed dotcc feature updates `C-SUPPORT.md`, with its fixture.
