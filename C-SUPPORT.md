# C language support in dotcc

Running tracker of what dotcc's grammar and libc cover today. Update this when a feature lands (add the fixture / test reference in **Notes**), when one moves from ❌ to 🟡 to ✅, or when something is decided out of scope (🚫 with reason).

**Default dialect: `c17`.** Select otherwise with `-std=<dialect>` (`c90`/`c99`/`c11`/`c17`/`c18`/`c23`) — see the CLI flag table in CLAUDE.md. `-std=` predefines `__STDC_VERSION__` (and friends) so headers can branch via `#if __STDC_VERSION__ >= …L`, and drives rule-2 keyword promotion (`bool`/`true`/`nullptr` become keywords only ≥c23). **By itself `-std=` stays permissive** — the parser is dialect-agnostic, so `//` comments, `_Bool`, designated initializers etc. are accepted regardless. **`-pedantic` / `-pedantic-errors` opt into the *rejection* gate** (gcc model): features newer than the selected `-std=` are diagnosed as warnings / errors. So the year tags below (`C99` / `C11` / `C23`) are descriptive for the default path, and become enforcement gates under `-pedantic`. Gated today: `_Bool` / `long long` / `ll`-suffix / designated init / `for`-init decl / `__func__` / variadic macros / mixed declarations-and-statements (C99), `_Static_assert` (C11), `enum : T` / `_Float128` / `#warning` (C23). Not gated: `//` comments (universal extension); K&R definitions are moot (not parsed) and VLAs are out of scope. Always-modern *output* is unaffected by `-std=` — dotcc emits real C# enums etc. regardless of input dialect.

**Legend**

| Marker | Meaning |
|---|---|
| ✅ | Supported. End-to-end fixture exists (or trivially derivable). |
| 🟡 | Partial — works in some shape but with caveats. **Notes** spells out what's missing. |
| ❌ | Not yet — on the roadmap. **Notes** points to the rough plan if known. |
| 🚫 | Out of scope. **Notes** explains why. |

Source of truth for the grammar: `DotCC.Lib/c.lalr.yaml`. Source of truth for the visitor lowering: `DotCC.Lib/CSharpEmitter.cs`. Source of truth for the runtime: `DotCC.Libc/`. When you flip a row's status, mention the fixture so the table can be re-validated by inspection.

## Lexical

| Feature | Status | Notes |
|---|---|---|
| Line comments `// …` | ✅ | `LINE_COMMENT` lexer rule, action `ignore` |
| Block comments `/* … */` | ✅ | `BLOCK_COMMENT`, classic no-alternation regex |
| Identifiers `[a-zA-Z_][a-zA-Z_0-9]*` | ✅ | `ID` token. A C identifier that collides with a **C# reserved keyword** (`new`, `lock`, `is`, `string`, `this`, `ref`, `object`, `in`, `out`, …) is `@`-escaped on emit (`@new`) — consistently at every declaration and reference (locals, params, function names + calls, struct fields + member access, labels, enum constants), since the escape is a pure function of the name. Fixtures `cs-keyword-idents/`, `keyword-true-false-ident/`. **Not escaped:** `null` (dotcc emits it as the bare C# `null` literal — the only expression that implicitly converts to any pointer type — so a variable named `null` is a residual edge; `true`/`false` are NO LONGER in this bucket — they lower to `1`/`0` and so escape correctly), and **type names** (struct/enum/union tags, typedef-names) — a `struct lock` tag isn't escaped yet. |
| Decimal int literal | ✅ | `NUM` token |
| Hex int literal `0xFF` | ✅ | Lexer rule above NUM (longest-match wins); visitor passes through — C# accepts identical syntax. Fixture `bitwise/` |
| Octal int literal `0755` | ✅ | Lexed by the decimal `NUM` rule (a `0`-prefixed run), then **converted** in `Visit(C.Num)` to its value (`0755` → `493`) — C# has no octal syntax (a leading `0` is plain decimal there), so emitting verbatim would silently mean 755. Digits validated `0`–`7` (`0789` → clear error). C89, no dialect gate. Fixture `octal-binary-literals/`. |
| Binary int literal `0b1010` | ✅ | C23. Lexer rules `0[bB][01]+` (± suffix) above the decimal rule; C# accepts `0b…` verbatim so the visitor passes it through. Gated as C23 under `-pedantic`. Fixture `octal-binary-literals/`. |
| Digit separators `1'000'000` | ✅ | C23. Each NUM digit-run rule allows a single `'` between digits (`D('?D)*` — never leading/trailing/doubled), in decimal, hex, and binary. The visitor strips them (C# uses `_` and needs none) before evaluating. Gated as C23 under `-pedantic`. Fixture `digit-separators/`. |
| Int suffixes `123u`, `123L`, `123ull` | ✅ | Lexer accepts any combination of `u`/`U`/`l`/`L` (one or more) after a decimal/hex/binary/octal digit run. Visitor's `CsIntSuffix` collapses to C#'s form: `u` → `u` (compiler resolves uint/ulong), one-or-more `L` → `L` (long; C# has no `ll`), `u` + `L`s → `UL` (ulong). |
| Float literal `1.5`, `1.5e10`, `1.5f` | ✅ | `FLOAT` token (mandatory `.` to disambiguate from `NUM`) |
| Float literal suffix `1.5L` (long double) | ❌ | The FLOAT lexer rule only accepts an optional `f`/`F`; `1.5L` lexes as `FLOAT(1.5)` + `ID(L)` → parse error. The `long double` *type* is supported (→ `double`); only this literal suffix isn't. Use an unsuffixed literal (`long double x = 1.5;`). `f`/`F` suffix works. |
| Char literal `'a'`, `'\n'`, `'\033'`, `'\x1b'` | ✅ | Five `CHAR` lexer rules (longest-match: hex `'\xNN'`, 3- and 2-digit octal, single-escape incl. 1-digit octal, plain). The visitor **decodes** the escape to its byte value: a plain printable char stays `(byte)'c'`, everything else (named escape, octal, hex, control) → `(byte)N`. Full escape set: `\a \b \e \f \n \r \t \v \0 \NNN \xNN \\ \' \" \?` (`\e` is the GNU ESC). Char bytes 0–255 all fine (it's a `(byte)` cast). Fixtures `small-ops/`, `string-char-escapes/`. |
| String literal `"…"` + adjacent concatenation `"a" "b"` | ✅ | `STRING` token regex `"(\\["\\nrtbf0v'/aex0-9]\|[^"\\])*"` (open quote, then backslash-escapes or non-quote-non-backslash, then close). **Adjacent literals concatenate** (C phase 6) via a left-recursive `StringSeq` → one `Primary`. Each segment's escapes are **decoded independently** to bytes, then concatenated, then re-emitted as a single greedy-safe C# `u8` literal: named escapes (`\n \t \r \\ \"`), and everything else as `\xHH` — and a `\xHH` is never left adjacent to a literal hex digit (C#'s `\x` is greedy), so octal/hex escapes and `"\x1b" "abc"`-style boundaries are correct. Non-ASCII source chars pass through (the u8 literal UTF-8-encodes them, matching C's UTF-8 source bytes). Fixtures `string-escape-quotes/`, `string-char-escapes/`. **Limitation:** a decoded escape byte > 0x7F (`"\xff"`, `"\377"`) can't be a single byte in a u8 literal (C# UTF-8-encodes it to two) → dotcc fails loudly rather than miscompile. Relies on IRx alternation in LALR.CC. |
| Wide string literal `L"…"`, `u"…"`, `U"…"` | 🚫 | dotcc is UTF-8-native — wide types add no value |
| Escape sequences `\n \t \\ \" \xNN` | ✅ | Recognized inside string literals (above) and char literals; passed verbatim into the C# UTF-8 literal so C# decodes them. |
| Trigraphs `??=` etc. | 🚫 | Removed in C23; never useful |
| Digraphs `<% %> :> :>` etc. | 🚫 | Same reasoning |

## Types

| Feature | Status | Notes |
|---|---|---|
| `int` | ✅ | Lowered to C# `int` |
| `char` | ✅ | Lowered to C# `byte` (so `char*` arithmetic walks bytes) |
| `short`, `unsigned short` | ✅ | `short` / `ushort`. Resolved via the `TypeSpecList` accumulator. |
| `long`, `long long`, `unsigned long`, `unsigned long long` | ✅ | All map to C# `long` / `ulong` (64-bit unconditionally in C#). MSVC-Windows's 32-bit `long` quirk is silently widened — dotcc-documented choice. |
| `signed` / `unsigned` qualifiers | ✅ | Free-order with the size and base keywords (the grammar uses a real-compiler-shape `TypeSpec` accumulator, not enumerated combinations). `unsigned char` → `byte`, `signed char` → `sbyte`. |
| `float` | ✅ | C# `float` |
| `double` | ✅ | C# `double` |
| `long double` | ✅ | Lowered to C# `double` — the CLI (dotcc's target) offers no wider IEEE float, so there's no native `long double` width to match; exactly mirrors the `long long` → `long` collapse. A documented narrowing on platforms whose native `long double` is wider (x87 80-bit, aarch64 binary128); for true 128-bit use `_Float128`. `double` still rejects `short` / sign / a second `long` (`long long double` errors). `%Lf`/`%Le`/`%Lg` print it via the `double` path (the `L` length modifier is parsed and ignored). The `LDBL_*` `<float.h>` macros alias the `DBL_*` values. Fixtures `factorial-longdouble/` (exact through 22!, gcc-oracle-validated), `float-limits/`. **Gap:** the `1.5L` *literal suffix* still doesn't lex (see the lexical table). |
| `_Float128` / `__float128` (C23) | ✅ | IEEE-754 **binary128**, lowered to the MIT software `DotCC.Libc.Float128` (clean-room, every op validated bit-for-bit against gcc's binary128). Standalone base type (no sign/size modifiers; `unsigned _Float128` etc. throw). Full correctly-rounded arithmetic `+ − * /`, `sqrt`, `fma`; relational `< > <= >=` (NaN-unordered); int/`double` conversions; and `printf` `%Lf`/`%Le`/`%Lg` at full quad precision (own BigInteger decimal formatter, no narrowing). **Complete `<math.h>` surface** — algebraic (`floor`/`ceil`/`trunc`/`round`/`copysign`/`fmod`/`remainder`/`cbrt`/`hypot`/`scalbn`/`ilogb`/`nextafter`) and transcendental (`exp`/`log`/`exp2`/`exp10`/`expm1`/`log2`/`log10`/`log1p`/`pow`/`sin`/`cos`/`tan`/`asin`/`acos`/`atan`/`atan2`/`sinh`/`cosh`/`tanh`/`asinh`/`acosh`/`atanh`/`rootn`) — implemented via a high-precision BigInteger fixed-point core and **validated against gcc's binary128 (`…l`) functions**: bit-exact for the correctly-rounded ops, ≤2–4 ULP for the transcendentals (gcc's own aren't correctly rounded). Also implements **`System.Numerics.IBinaryFloatingPointIeee754<Float128>`** (decimal Parse/ToString, conversions, generic-math interop). `__float128` is the GNU spelling (accepted by dotcc; gcc only defines it on x86, so the aarch64 gcc oracle covers `_Float128`). Fixtures `float128-basic/` (arithmetic) + `float128-print/` (`%Lf` of 1/3 to 40 places). MSVC's cl.exe has no `_Float128`, so those fixtures opt out of the MSVC oracle. |
| `void` | ✅ | C# `void`; only valid as return type or `void*` |
| `_Bool` / `bool` (C99 stdbool.h) | ✅ | `_Bool` is a TypeSpec keyword lowered to the integer-typed value struct **`Libc.CBool`** (NOT C# `bool`), so C's defining `_Bool` semantics hold at every boundary via implicit conversions: storing any scalar normalizes to 0/1, and a bool converts freely to `int` in arithmetic, assignment, return, argument, and `%d` positions (`int x = b;`, `b + 1`, `return b;` from an `int` function all compile and behave as C). `sizeof(_Bool)` is 1. Synthetic `stdbool.h` defines `bool` → `_Bool` and `true`/`false` → the integer constants `1`/`0` (their C values, normalized through `CBool`). Pointer stores work too: `_Bool b = somePointer;` (meaning `somePointer != NULL`) is covered by a `void*` → `CBool` conversion operator — a typed `T*` reaches it through the standard `T* → void*` conversion (one standard + one user-defined step, which C# permits; C# does **not** forbid pointer conversion operators). Combinations with sign/size specifiers (`unsigned _Bool` etc.) throw `CompileException`. Fixtures `bool-stdbool/`, `bool-as-int/`, `bool-from-pointer/`. Under **`-std=c23`** `bool` is a first-class keyword: `DialectKeywordRewriter` promotes bare `bool` → `_Bool` with no `<stdbool.h>` include needed (pre-C23 it stays an ordinary identifier, so the macro path and old code using `bool` as a name both keep working). |
| `true` / `false` / `nullptr` (C23 keywords) | ✅ | Under **`-std=c23`** these are first-class keyword constants — `DialectKeywordRewriter` promotes them onto dedicated `TRUE`/`FALSE`/`NULLPTR` grammar terminals (no lexer rule — same rule-2 gating as `bool`), lowering to the integer literals `1`/`0` (normalized through `CBool` when stored to a `_Bool`) and C# `null`, with no `<stdbool.h>`/`<stddef.h>` include. Pre-C23 they stay ordinary identifiers; since `true`/`false` no longer emit as C# keywords, a variable named `true`/`false` is correctly `@`-escaped (`int true = 5;` → `int @true = 5;` — fixture `keyword-true-false-ident/`). Fixture `c23-constants/` (gcc oracle under `-std=c2x`; opts out of MSVC, which has no C23 frontend). |
| Pointer types `T*`, `T**`, … | ✅ | Composes left-to-right via `TypePtr` production |
| Array decl `T arr[N]` | ✅ | Lowered to `T* arr = stackalloc T[N]` so block-scoped automatic arrays match C's lifetime + subscript semantics; fixture `array-sum/`. A non-constant 1-D extent (`int a[n]`) lowers to `stackalloc T[n]` (VLA-ish). |
| Multi-dimensional array `T a[D1][D2]…` | ✅ | C# `stackalloc` is 1-D, so dotcc **flattens** to one contiguous block `stackalloc T[D1*D2*…]` (row-major, like C) via the `ArrDims` dimension list, tracking the shape as a nested `CType.Arr`. A **partial** subscript `a[i]` (dimensions remain) rewrites to `a + i*stride` (stride = element count of the remaining sub-array); a **full** subscript `a[i][j]` is the flat element. The nested `sizeof` length idiom works (`sizeof(a)/sizeof(a[0])` = rows, `sizeof(a[0])/sizeof(a[0][0])` = cols). All dimensions must be compile-time constants (a non-constant multi-dim extent errors clearly). Fixture `multidim-array/`. **Gap:** nested-brace initializers (`int m[2][2] = {{1,2},{3,4}}`) aren't supported yet — initialize via loops, or use a flat `int m[4] = {…}`. |
| Array param decay `T arr[]` / `T arr[N]` | ✅ | Both forms lower to `T* arr` per C99 §6.7.5.3p7. The size in the sized form is informational only (C compilers don't enforce it at call sites). Fixture `array-param-decay/` |
| Variable-length arrays (C99) | 🚫 | Out of scope — rarely used in practice, made *optional* in C11/C17, and a poor fit for a managed runtime (unbounded stack growth, no `alloca`-style guarantees). Idiomatic code uses `malloc`. A non-constant 1-D extent (`int a[n]`) still *incidentally* lowers to `stackalloc T[n]` as a side effect of the ordinary array lowering, but that's not a guaranteed feature — multi-dim VLAs and `sizeof` of a VLA aren't supported, and a multi-dim non-constant extent errors. |
| `struct` declaration | ✅ | Lowered to `unsafe struct ID { public T field; … }`; fields public to match C accessibility. Emitted via a visitor side channel into the type-decl section (C# requires types after top-level statements). Fixture `struct-point/` |
| `struct` member access `.` / `->` | ✅ | Both lowered verbatim; C# accepts `->` on struct pointers in unsafe context (identical semantics to C). Fixture `struct-point/` covers both forms |
| Bit-fields `Type ID : width` | 🟡 | C# has no bit-fields. **Lossy lowering:** a named bit-field becomes a FULL field of its declared type (the width is dropped, recorded in a `// bit-field :N` comment); an anonymous bit-field (`unsigned : 2;`, padding) emits nothing. **Correct for values that fit the declared width** — the common case for flags/small fields — and `f.field` reads/writes/printf work normally. **Caveats (documented):** no truncation/wrap on overflow (`f:3 = 8` stays 8, C wraps to 0), and the struct's `sizeof`/layout differ from C (separate fields, not packed). A faithful packed lowering (backing storage + masked accessors) is future work. Grammar: `Member → Type ID ':' E ';'` (named) / `Type ':' E ';'` (anonymous), disambiguated from a plain member by the `:`. Fixture `bitfields/`. |
| `struct ID` as a type | ✅ | `Type → 'struct' ID` lowers to bare `ID` in C# usage position (C# doesn't require `struct` prefix outside the declaration) |
| Forward struct decl `struct Node;` | ✅ | `Fn → 'struct' ID ';'` emits nothing in C# (types hoist regardless of declaration order). Needed for self-referential types where the pointer use precedes the full definition in a header. |
| `malloc`/`free` → stack-value peephole | ✅ | `S* p = (S*)malloc(sizeof(S))` with a matching `free(p)` in the same function, where `p` is used **only** through `->` (never returned, passed, address-taken, indexed, compared, or reassigned), lowers to a stack value `S p = new S();` — the `->` accesses become `.` and the `free` is dropped. `new S()` value-initialises on the stack (no native heap alloc). Recognition is structural via a two-pass analysis (markers + `->`/free/total ref-count balance per `(function, var)`), not text-matching; restricted to single-declarator decls of known struct types. Any escaping use keeps the low-level heap form. Fixture `malloc-stack-promote/`. |
| Self-referential structs (linked list / tree) | ✅ | Works without special handling — `struct Node { struct Node* next; }` lowers cleanly because `Type → 'struct' ID` emits just the name and the resulting `Node* next` field references the struct being defined. Fixture `struct-linked-list/`. |
| Struct return-by-value | ✅ | `T f() { T x; …; return x; }` works — C# structs are value types, copy-on-return matches C semantics. Fixture `struct-linked-list/` (uses `make_pair` returning a struct). |
| `sizeof(Type)` | ✅ | Lowered verbatim to C# `sizeof(T)`; valid in unsafe context for any unmanaged type (which dotcc's structs all are). Fixture `struct-point/` |
| `sizeof expr` (expression form) | ✅ | Backed by a **type-synthesis layer**: expression visitors propagate a `CType` (Sized / Arr) up through `EmitContent.Text.Ty`, and `Visit(C.SizeofExpr)` reads the operand's `CType` to emit the size. Arrays compute `count * sizeof(element)` — dotcc lowers `T arr[N]` to a C# pointer (`stackalloc`), so C# `sizeof(arr)` would be wrong; everything else defers to C# `sizeof(type)`. Synthesized today: variable (incl. array element+count via `_localArrayInfo`), subscript, deref, cast, paren, call result, and literals (`'a'`→`int`, `"…"`→char[len+1], `42L`→long, `3.14`→double). The array-length idiom `sizeof(a)/sizeof(a[0])` works. Grammar: `Unary → 'sizeof' Unary` (conflict-free via the TYPE_NAME lexer hack). Fixture `sizeof-expr/` (gcc-oracle-validated for all sizes). **Gaps** (synthesis returns null → clear CompileException): struct/union member access (`sizeof(s.field)` — field types not tracked yet) and arithmetic-result `sizeof(a+b)`. |
| `union` | ✅ | Lowered to C# struct with `[StructLayout(LayoutKind.Explicit)]` + `[FieldOffset(0)]` on each member — matches C's overlapping-storage semantics. Plain non-init `union U u;` becomes `union U u = default;` so C#'s definite-assignment is satisfied (overlapping fields can't be written individually). Fixture `union-bits/` |
| `enum` | ✅ | Lowered to a **real C# `enum EnumName : int { Member = VAL, … }`** (all dialects) in the type-decl section — preserving the C type name, `switch`/`case`, type-safe params, and `ToString`. Each enumerator → enum-name is tracked in `_enumerators`; an unqualified `Member` lands as `EnumName.Member` at the `Var` visitor (so user code keeps writing bare `Red`), and an enum-typed variable read is tagged with its enum type. **int↔enum casts** that C requires but C# doesn't are inserted by an enum-typing synthesis (`EmitContent.Text.EnumType`): an enum operand of any arithmetic/bitwise/relational/shift operator **decays to `(int)`** (so the op is pure-int C semantics, never C#'s asymmetric enum-operator rules), and a non-enum value stored into an enum-typed slot — decl init, assignment, `return`, `printf` `%d` arg, array index, `Cond.B`, compound `\|=`/`&=` — is cast back `(EnumName)` / decayed `(int)` at the sink. Enum members stay compile-time constants, so `case Red:` and `int a[Blue]` still work. Auto-numbering: each item without an explicit value takes `previous + 1`. A local/param that **shadows** an enumerator resolves to the local (the per-function declared-names set guards the rewrite). **C23 fixed underlying type** `enum Name : Type { … }` → `enum Name : <mapped base>` (`unsigned char` → `byte`, etc.). Fixtures `enum-day/`, `enum-shadow/`, `enum-flags/`, `enum-from-int/`, `enum-underlying-c23/`. Known gaps (need a callee-param-type pass): a regular call passing an enum to an `int` param (or int to an enum param) isn't cast; struct/union fields of enum type aren't yet enum-typed on read. |
| `typedef` | ✅ | `TypeNameRewriter` (a `RewritingTokenStream` slotted after the preprocessor) implements the classic "lexer hack" — promotes `ID` → `TYPE_NAME` for any name previously bound by `typedef`. Productions: `Fn → typedef Type ID ;` (simple alias, including `typedef struct Foo Foo;`) and `Fn → typedef struct ID { MemberList } ID ;` (struct def + alias). Emitter lowers simple aliases to C# `using unsafe Alias = Underlying;` at file scope; struct-with-alias becomes a single `unsafe struct Alias { … }`. Fixture `typedef-point/` |
| `const T` / `volatile T` qualifiers | ✅ | Parsed as `TypeSpec` keywords and **dropped** by `ResolveTypeSpec` (C# has no `readonly`-local / volatile-access model for the unsafe-pointer form), so `const char *p` lowers exactly like `char *p`. Both C90, no dialect gate. Covers the leading/intermixed position (`const int`, `const char *`, `unsigned const long`) AND the trailing pointer-qualifier position (`int * const p`, via the post-`*` rules below). Fixture `c-idioms/`. |
| `restrict` (C99) / `int * const` (pointer qualifiers after `*`) | ✅ | A qualifier after the pointer star — `int *restrict p`, `int * const p`, `int * volatile p` — parses and is **dropped** (the type is just the pointer). dotcc has no aliasing model, so `restrict` carries no semantics today; a future optimization could map a `restrict` parameter to a by-ref / no-alias hint. GNU `__restrict` / `__restrict__` lex to the same token. Fixture `restrict-qualifier/`. |
| Function pointer `T (*fp)(args)` | ✅ | Two forms. **typedef'd**: `typedef int (*Name)(args);` → `using unsafe Name = delegate*<args, int>;`. **Standalone local declarator** (the famously-tricky embedded-name form): `int (*op)(int, int) [= E];` → a `delegate*<int, int, int>` local — conflict-free because the `( * ID )` shape is distinct from every `Type ID` declarator. A bare function-name initializer (`= add`, which C decays to a pointer) gets the `&` C# requires; `&add` passes through; `&function_name` is C#'s `&LocalFunction`. Calling `op(x, y)` works (C# function-pointer invoke, unsafe). Fixtures `fnptr-sort/` (typedef'd `Comparator`), `fnptr-local/` (standalone). **Gaps:** the standalone fn-ptr as a *parameter* isn't parsed yet (use the typedef'd form for callback params); a bare function name in an *assignment* / call argument still needs explicit `&` (the callee/lvalue-param-type gap); pointer-to-array `int (*p)[3]` isn't supported. |
| Unnamed (abstract) parameters `f(int, char*)` | ✅ | C allows a name-less parameter in declarations and function-pointer types; C# requires a name, so the visitor synthesizes a unique one (`_p0`, `_p1`, …). `Param → Type`. Lets `int (*op)(int, int)` and unnamed-param prototypes parse (the typedef fn-ptr form no longer needs `(int a, int b)`). |
| Compound literals `(int[]){1,2,3}` (C99) | ❌ | Depends on array support |

## Operators

| Feature | Status | Notes |
|---|---|---|
| Arithmetic `+ - * /` | ✅ | `Add`/`Mul` productions |
| Modulo `%` | ✅ | At `Mul` precedence; fixture `fizzbuzz/` |
| Unary `+ - *` (deref) `&` (addrof) | ✅ | `Unary` productions |
| Logical `&& \|\|` | ✅ | At `LAnd` / `LOr` precedence. Result lowers to `CBool` (C's int 0/1), so `int f = a && b;` works — see Comparison row. |
| Logical `!` (unary not) | ✅ | `Unary → '!' Unary` lowers to `(Cond.B(E) ? 0 : 1)` so the result is `int` (matching C's `!x` yielding 0 or 1, not bool). Fixture `small-ops/` |
| Comparison `< > <= >= == !=` | ✅ | `Rel` and `Equ` productions. In C these yield **`int` 0/1**, not bool, so the result must be usable in any integer position (`int x = a > b;`, `(a>0)+(b>0)`, `return a==b;` from an int function, `printf("%d", a<b)`). C# `<`/`==` produce `bool`, so dotcc casts the result to `CBool` (CBool→int carries it into value positions; a `Cond.B(CBool)` overload carries it into conditionals; nested comparisons like `(a>b)==(c>d)` resolve via CBool→int on both operands). Same wrap on `&&`/`\|\|`. Fixture `comparison-as-int/`. |
| Bitwise `& \| ^ << >>` | ✅ | `BOr` / `BXor` / `BAnd` / `Shift` non-terminals inserted at proper C precedence; fixture `bitwise/` |
| Bitwise `~` (unary) | ✅ | `bNot` action in `Unary`; fixture `bitwise/` |
| Assignment `=` | ✅ | Right-associative via `rightmost` precedence group |
| Compound assign `+= -= *= /= %=` | ✅ | In the rightmost group with `=`; fixture `factorial-for/` uses `*=`, `fibonacci/` uses `+=` via local var |
| Compound assign `&= \|= ^= <<= >>=` | ✅ | All five in the rightmost-= group; fixture `bitwise/` covers `\|=`, `^=`, `<<=`, `>>=` |
| Increment / decrement `++` / `--` (pre and post) | ✅ | `preInc`/`preDec` in `Unary`, `postInc`/`postDec` in `Postfix`; fixture `factorial-for/`, `fibonacci/`, `fizzbuzz/` (all use `i++`) |
| Ternary `c ? a : b` | ✅ | `E → LOr '?' E ':' E` in the rightmost E group. Lowers to `(Cond.B(c) ? a : b)`. Right-associative — chained ternaries `a ? 1 : b ? 2 : 3` work naturally. Fixture `small-ops/` |
| Comma operator `a, b` | ✅ | Full comma operator via a new `Expr` tier above assignment `E` (`Expr → Expr ',' E`), used in the two C `expression` positions: the parenthesized primary `( Expr )` and the expression statement `Expr ;`. C# has no comma operator, so: the **value form** `(a, b, c)` lowers to a tuple `(a, b, c).Item3` (C# evaluates tuple elements left-to-right — same sequence-point order, value is the last; ≤7 operands, longer chains error clearly); the **statement form** `a, b;` (result discarded) splits into sequential statements. Call-argument / initializer commas stay on `E`, so they remain separators (`f(a, b)` is two args; `f((a, b))` is one comma-operator arg). `for`-init/update keep their own `CommaExprList` passthrough (`for (i=0, j=10; …; i++, j--)`). **Limitations:** pointer/void operands can't go in a C# tuple → surface as a Roslyn type error (loud, not silent); a bare-value statement operand (`a, b;` with no side effect — pointless C) hits CS0201. Fixtures `comma-operator/`, `comma-for-loop/`. |
| `sizeof(type)` and `sizeof expr` | ✅ | Both forms supported — `sizeof(Type)` directly; `sizeof expr` via the type-synthesis layer (see the `sizeof expr` row in the Types table). |
| Subscript `arr[i]` | ✅ | Emitted as-is in C# unsafe context (pointer subscript matches C semantics); fixture `array-sum/` |
| Cast `(T)expr` | ✅ | `Unary -> '(' Type ')' Unary` |
| Call `f(a, b)`, `f()` | ✅ | `Postfix` productions |
| Member access `.` / `->` | ❌ | Depends on `struct` |
| `_Alignof` (C11) | ❌ | C11 — low priority |
| `_Generic` (C11) | ❌ | C11 type-generic dispatch — complex; low priority |

## Statements

| Feature | Status | Notes |
|---|---|---|
| Expression statement `expr;` | ✅ | `stmtExpr` |
| Declaration statement `int x;`, `int x = 0;` | ✅ | `stmtDecl` |
| Block `{ … }` | ✅ | `block` / `blockEmpty`. **Block-scope local shadowing** is handled: C lets two same-named locals live in nested-vs-enclosing block scopes (e.g. a `v` inside an `if` block and a separate `v` in the function body, in either textual order), but C# rejects the pair as **CS0136**. dotcc alpha-renames the collision — a scope stack (raw name → emitted C# name) maps each declaration, and a colliding one gets a fresh `name__k`; references resolve through the stack so each use binds to the right declaration. The block-entry hook is an epsilon marker non-terminal `ScopeEnter` reduced just after `{` (and after a `for (` decl header, so a `for (int i …)` that shadows an outer `i` is also renamed apart); the matching pop is the `block` / `stmtForDecl` action. Params keep their spelling — a nested local that collides with a param is the one renamed. Fixture `calc/` (a recursive-descent evaluator whose `factor()` has the canonical inner-vs-body `v` collision). |
| `if` / `if`–`else` | ✅ | Dangling-else resolved by `rightmost` precedence group |
| `while (e) stmt` | ✅ | `stmtWhile` |
| `do { … } while (e);` | ✅ | `Stmt → 'do' Stmt 'while' '(' E ')' ';'`. Cond wrapped with `Cond.B(...)` like other loops. Fixture `small-ops/` |
| `for (init; cond; incr) stmt` | ✅ | Init shapes: decl (`stmtForDecl`), expr (`stmtForExpr`), or empty (`stmtForNoInit`). **All clauses optional** via the nullable `ForCond`/`ForPost` non-terminals — `for (;;)` (empty cond → C# `true`), `for (i=0;;i++)`, `for (; i<n; )` all parse. Fixtures `factorial-for/`, `fibonacci/`, `fizzbuzz/`, `c-idioms/` (`for (;;)` + break). |
| `switch (e) { case … }` | ✅ | `CaseList` non-terminal owns the case chain; each `case`/`default` clause is `case E : StmtList` / `default : StmtList`. Emitted as C# switch verbatim — user writes `break;` per case (matches C convention and C# requirement); fixture `switch-day/` |
| `break;` | ✅ | `stmtBreak`; fixture `loop-break/` |
| `continue;` | ✅ | `stmtContinue`; fixture `loop-break/` |
| `return;`, `return e;` | ✅ | `stmtReturn` / `stmtReturnVoid` |
| `goto label;` + labels | ✅ | Direct lowering — C# accepts identical `goto label;` and `label: Stmt` syntax with the same forward-reference semantics inside a method body. Productions: `Stmt → 'goto' ID ';'` and `Stmt → ID ':' Stmt`. Fixture `goto-cleanup/` exercises the canonical "goto-out" error-cleanup ladder. |
| Empty stmt `;` | ✅ | `Stmt → ';'` — emits a bare semicolon. Needed so labels can attach to the end of a block (`end: ;` is valid pre-C23). |

## Declarations

| Feature | Status | Notes |
|---|---|---|
| Function definition `T f(args) { … }` | ✅ | `funcDef` / `funcDefNoArgs` / `funcDefVoidArgs` (the C-correct `(void)` form). Static variants for each. |
| Function prototype `T f(args);` | ✅ | `protoDef` / `protoDefNoArgs` / `protoDefVoidArgs` — emitted as empty (C# methods hoist). Static variants for each. |
| Multiple declarators `int x, y;` | ✅ | `Decl → Type DeclItemList`; `DeclItem → ID | ID = E`. Plain item lowers to `name = default` (so C# definite-assignment is satisfied for struct/union fields). Mixed init / no-init works (`int a = 1, b, c = 3;`). Array declarators stay single-only (the lowering to `T* arr = stackalloc T[N]` doesn't compose with peers). Fixture `multi-decl/` |
| File-scope variable `T name;` / `T name = E;` / `T a, b = 5, c;` | ✅ | C globals at file scope (e.g. `jmp_buf env;` for setjmp). Reuses the same `DeclItemList` non-terminal that block-scope `Decl` uses, so the multi-declarator forms (`int a, b;`, `int x = 1, y = 2;`, `int p, q = 5, r;`) work identically. Each declarator collects into a `public static unsafe T name [= expr];` field inside a `static unsafe class DotCcGlobals` declared in the type-decls section; `using static DotCcGlobals;` in the shell brings the names into scope unqualified for every emitted function. Aliases that resolve through `typedef` to predefined C# reference types (currently just `jmp_buf` → `Libc.LongJmpToken`) get auto-initialized per-declarator with `new T()` so reference-equality dispatch (longjmp's exception filter) works. Fixtures `setjmp-cleanup/`, `file-scope-multi-decl/`. |
| Initializer lists `int arr[] = {1, 2, 3}`, `Point p = {1, 2}` | ✅ | Array form lowers to `T* arr = stackalloc T[]{ … }` (both sized `int x[3]` and implicit-size `int x[]`). Struct positional init lowers to `Point p = new Point { x = 1, y = 2 };` — the emitter tracks field names per struct in `_structFields` (populated by `StructDef` / `TypedefStruct` / `UnionDef` draining the names pushed by each `StructMember` visit) and looks them up to rebuild the named-initializer form C# requires. Partial init `Vec3 v = {7}` zeroes the trailing fields (C# default semantics matching C). An **optional trailing comma** (`{1, 2, 3,}`) is accepted in all brace-init forms (array, struct positional, and designated) via the left-recursive `InitList` / `MemberInitList` rules — kept separate from the call-argument `ArgList` (where a trailing comma is illegal). Fixtures `array-init/`, `struct-init/`, `c-idioms/`. **Gap:** the C23 empty initializer `{}` (≥1 element required) — see the C23 table below. |
| Designated initialisers (C99) `{.x = 1}` | ✅ | New `MemberInit` / `MemberInitList` grammar productions; `Decl → Type ID '=' '{' MemberInitList '}'` distinguishes from the positional form by lookahead on `.` after `{`. User provides field names directly so no `_structFields` lookup is needed at emit time. Omitted fields zero-fill per C99 (matches C#'s struct object-initializer default behavior). Fixture `struct-linked-list/` (designated init for `Pair`). |
| Storage classes `static`, `extern`, `auto`, `register` | 🟡 | **`static` on functions** — internal linkage (no `[UnmanagedCallersOnly]` export wrapper in library mode). **`static` on variables (file-scope + function-static)** — ✅ both supported: each lowers to a `public static unsafe` field in `DotCcGlobals` (static storage duration → persists across calls; once-only constant init → exact match for a C# static field initialiser). File-scope keeps its name (internal linkage is a no-op for a non-exported variable); a function-static is **mangled** `__static_<fn>_<name>` and in-function references are rewritten to it, so two functions can each declare `static int x`. Scalars + plain (no-init) structs via `Type DeclItemList`; static arrays / aggregate-init statics not yet (their `stackalloc` lowering doesn't compose with static lifetime). Fixture `static-locals/`. **`extern`** — ✅ supported: an extern variable declaration (`extern int x;`) and function prototype (`extern int f(int);`) DECLARE without defining, so they emit nothing (the storage / body lives in another TU; emitting a `DotCcGlobals` field would double-define against the real definition); an extern function *definition* (`extern T f(…) { … }`) is a normal definition (extern is the default function linkage). Three Fn productions reusing `FnSig` for the function forms. References resolve because dotcc compiles all TUs into one program and the definition's field/method hoists. Fixture `extern-linkage/` (two TUs: one defines `g_count`/`bump`, the other extern-declares + uses them). **`auto`** — ✅ both meanings: the redundant pre-C23 storage class (`auto int x`) is dropped, and the C23 inference form (`auto x = E`) lowers to `var` (see the C23 table). `register` is 🚫. |
| `inline` (C99) | ❌ | Cosmetic — emit `[MethodImpl(AggressiveInlining)]` |
| Mixed decls and statements (C99) | ✅ | Already supported — `Stmt` accepts `Decl` |

## Preprocessor

| Feature | Status | Notes |
|---|---|---|
| `#include "header.h"` | ✅ | Quoted-form; resolves against `-I` dirs + `.c`'s own dir + synthetic `SystemHeaders` |
| `#include <header.h>` | ✅ | Angle-form supported by reassembling the fragmented `<`, name, `.`, ext, `>` tokens back into a filename — no lexer state change needed. Resolved against the same path stack as quoted-form (`-I` dirs + .c sibling dirs + synthetic SystemHeaders). |
| `#define NAME body` | ✅ | Macro body stored as Items; substituted via `Rewrite` hook with a hide-set-guarded rescan so chained macros (`#define B A` where `A` is `#define A 42`) transitively resolve at use site. Self-referential `#define A A` doesn't infinite-loop (hide set prevents the cycle, per the C standard). Empty body OK (defined-as-marker). |
| `#define NAME(args) body` | ✅ | Function-like macros via `MacroExpander` (a `RewritingTokenStream` subclass — same pattern as `PreprocessorTokenStream` and `TypeNameRewriter`). Sits between the preprocessor and the typedef rewriter; uses `TryReadNext` lookahead to detect `(` after a macro name, paren-balanced arg collection (commas inside nested parens don't split), per-parameter substitution into the body, and a multi-pass rescan with a per-call hiding set so `#define CLAMP(x, lo, hi) MAX(lo, MIN(x, hi))` fully expands. Object-like still handled by `CPreprocessor.Rewrite` upstream. **Whitespace-aware detection**: `#define NAME(x) body` is function-like ONLY when `(` is immediately adjacent to NAME (no space) — `#define NAME (expr)` (with space) is object-like with body `(expr)`. Disambiguation uses token positions. Fixture `macro-funclike/` (MSVC-oracle-validated). |
| `#undef NAME` | ✅ | `OnUndef` |
| `#if <expr>` | ✅ | Full C constant-expression evaluator (upstream in LALR.CC's `PreprocessorExpressionEvaluator`): integer literals (decimal `0x...` hex), `defined(NAME)` and `defined NAME`, arithmetic (`+ - * / %`), comparison (`< > <= >= == !=`), logical (`&& || !`), bitwise (`& | ^ ~ << >>`), ternary (`?:`), parens. Object-like macros expand before evaluation, so `#if VERSION >= 2` works when `VERSION` is `#define`'d. Fixture `preproc-cond/`. |
| `#ifdef NAME`, `#ifndef NAME` | ✅ | Built-in conditional engine consults `IsDefined`; fixture `hello/` uses `#ifndef` header guards |
| `#else`, `#elif`, `#endif` | ✅ | All three supported. `#elif` participates in the if/elif/else chain — the runtime branch stack tracks `(emitting, anyEmittedYet)` per entry, so once any arm is true the rest of the chain stays suppressed regardless of expression results. Fixture `preproc-cond/`. |
| `#error msg` | ✅ | Aborts compilation by throwing `CompileException` (same surface as a parse failure); frontend maps to a non-zero exit with `dotcc: #error: <msg>`. |
| `#warning msg` (C23) | ✅ | Emits `dotcc: #warning: <msg>` to stderr and continues. Same path as `#error` but non-fatal. |
| `#pragma …` | 🟡 | `#pragma once` is honored — `CPreprocessor` tracks `_pragmaOnceFiles` keyed by the currently-being-processed filename, short-circuits subsequent `#include` of the same name. All other pragmas silently ignored (matches the C convention: unknown pragmas don't break the build). |
| Multiple-include optimization (controlling-macro detection) | ✅ | After the first `#include` of a file, `CPreprocessor.DetectControllingMacro` scans the raw source for the standard `#ifndef X / #define X / ... / #endif` wrapping pattern (with only whitespace + comments outside the outer guard). If detected, the result is cached on `_fileGuards[filename] = X`; subsequent `#include`s of the same file check `IsDefined(X)` and short-circuit without re-opening or re-lexing. Same optimization gcc/clang call "controlling macro detection". `CPreprocessor.IncludeOptimizationHits` counts the short-circuits (used by `PreprocessorIncludeGuardTests` to assert the optimization actually fires). |
| `#line N`, `#line N "file"` | 🚫 | Useful only for generated-code lineage which dotcc doesn't preserve |
| `##` token-pasting | ✅ | `MacroExpander.Substitute` recognizes `LHS ## RHS` patterns in function-like bodies: resolves each operand (formal-param → arg tokens, else literal token), pastes the last LHS token with the first RHS token into one ID-shaped token carrying the concatenated content. After paste, the multi-pass rescan re-checks the result — so `MAKEFOO(1)` → `foo_1` → resolves to the matching `#define foo_1 100`. Fixture `macro-ops/`. |
| `#` stringification | ✅ | `# PARAM` inside a function-like body emits a STRING token built from the arg's source text. Token contents joined with single spaces; `"` and `\` inside content are backslash-escaped so the resulting literal is well-formed. Fixture `macro-ops/`. |
| `__FILE__`, `__LINE__`, `__func__` (C99) | ✅ | `__FILE__` + `__LINE__` are preprocessor-time: `CPreprocessor.Rewrite` synthesizes a STRING token (containing the active filename, set per translation unit via `SetActiveFilename`, also tracked through nested `#include`s) and a NUM token (from the use site's `Position.Line`), respectively. `__func__` is visitor-time: `Visit(Var)` emits a unique placeholder, then each `Visit(FuncDef*)` variant string-replaces it in the body with the enclosing function's name wrapped in the dotcc `L("name\0"u8)` idiom. Fixture `predefined-ids/` (MSVC-oracle-validated for byte-identical line/file/function output). |
| Variadic macros `...` / `__VA_ARGS__` (C99) | ✅ | `OnDefine` detects a trailing `...` in the param list and sets `MacroDef.IsVariadic`. At expansion, extras beyond the named-param count are comma-joined and bound to the magic name `__VA_ARGS__` in the substitution map; references to that name in the body expand to the joined extras. Fixture `macro-ops/` (variadic `LOG(fmt, ...)` shape). |

## libc (`DotCC.Libc/`)

The runtime surface dotcc-emitted programs link against. Each function routes to a BCL primitive. Implementations live in `DotCC.Libc/*.cs`; the same source compiles into `DotCC.Libc.dll` for unit tests AND is embedded as a resource into `DotCC.Lib.dll` (spliced into every emitted program by `BuildShell` — single source of truth). Once `DotCC.Libc` is published to NuGet, the embedding goes away and emitted programs reference the package directly.

**Design rule: reentrant by default.** Where the C standard offers a stateful and a reentrant variant of the same function (`strtok` / `strtok_r`, `rand` / `rand_r`, `asctime` / `asctime_r`, `localtime` / `localtime_r`, etc.), dotcc implements the reentrant form as the primitive and exposes the stateful form (if at all) as a thin wrapper using `[ThreadStatic]` storage. Hidden static state in libc is a notorious source of multithreading bugs; emitted dotcc programs target .NET where threading is cheap and idiomatic, so the reentrant shape is the right default. Real C programs that need the C89 stateful form keep working through the wrapper, but the docs steer users to the `_r` variant.

### `stdio.h`

| Function | Status | Notes |
|---|---|---|
| `printf` | ✅ | Fluent `PrintfBuilder`; specs `%d %i %x %X %c %f %e %g %s %%` |
| `fprintf` | ✅ | Primitive; takes `TextWriter stream` (the C# stand-in for `FILE*`) |
| `sprintf` | ✅ | Writes formatted output into `byte*`, NUL-terminates |
| `snprintf` | ✅ | Bounded sprintf; returns count-that-would-have-been-written per C99 |
| `scanf`, `fscanf`, `sscanf` | ✅ | `ScanfReader`; specs `%d %i %f %e %g %s %c` |
| `puts`, `fputs` | ✅ | `puts` adds newline; `fputs` doesn't |
| `getchar`, `fgetc`, `getc` | ❌ | Trivial — wrap `Console.In.Read()` |
| `putchar`, `fputc`, `putc` | ❌ | Trivial — wrap `Console.Write(char)` |
| `gets` | 🚫 | Removed in C11 — never add |
| `fgets` | ❌ | Read line into `byte*` buffer with cap |
| `fopen`, `fclose` | ❌ | Wrap `File.Open` returning a managed-FILE handle (likely a small class) |
| `fread`, `fwrite` | ❌ | Wraps `Stream.Read`/`Write` |
| `fseek`, `ftell`, `rewind` | ❌ | `Stream.Seek` / `Stream.Position` |
| `feof`, `ferror`, `clearerr` | ❌ | Track via the managed-FILE handle |
| `perror` | ❌ | Wrap with `Console.Error.WriteLine` |
| `remove`, `rename` | ❌ | `File.Delete` / `File.Move` |
| `tmpfile`, `tmpnam` | ❌ | Wrap `Path.GetTempFileName` |
| `setbuf`, `setvbuf` | 🚫 | No equivalent control over `Console` buffering |

### `stdlib.h`

| Function | Status | Notes |
|---|---|---|
| `malloc`, `free` | ✅ | `NativeMemory.Alloc` / `Free` |
| `calloc`, `realloc` | ❌ | `NativeMemory.AllocZeroed` / `Realloc` |
| `exit`, `abort`, `_Exit` | ❌ | `Environment.Exit` / `Environment.FailFast` |
| `atof` | ✅ | `strtod(s, NULL)` — leading-double parse. Fixture `strtod-parse/`. |
| `atoi`, `atol` | ❌ | `int.Parse` / `long.Parse` (invariant culture) |
| `strtod` | ✅ | Parses a leading double (sign / decimal / `e`-exponent / C99 `inf`/`nan`), skips leading whitespace, sets `endptr` to the first unconsumed byte (so a buffer of numbers can be walked); `double.Parse(NumberStyles.Float, invariant)` core. Fixture `strtod-parse/`; `LibcTests`. |
| `strtol`, `strtoul`, `strtoll` (C99) | ❌ | Integer forms with error-position out param + base. |
| `abs`, `labs`, `llabs` (C99) | ❌ | `Math.Abs` |
| `rand`, `srand` | ❌ | Thread-local `Random` to match C's process-global seeding |
| `qsort`, `bsearch` | ❌ | Generic compare-callback; AOT-clean via function pointers |
| `getenv` | ❌ | `Environment.GetEnvironmentVariable` |
| `system` | ❌ | `Process.Start` (frontend exe is fine with this; emitted programs maybe) |
| `div`, `ldiv`, `lldiv` | ❌ | Quotient + remainder struct |

### `string.h`

Synthetic header at `DotCC.Lib/include/string.h` declares the surface; implementations live in `DotCC.Libc.Libc`. Fixture `string-h-basic/` exercises every declared function end-to-end with MSVC oracle validation.

| Function | Status | Notes |
|---|---|---|
| `strlen` | ✅ | Pointer loop; declared in `<string.h>`. Returns `int` (dotcc-specific — real C returns `size_t`; portable code should cast). |
| `strcmp` | ✅ | Pointer loop; declared in `<string.h>`. |
| `strncmp` | ❌ | Bounded `strcmp` |
| `strcpy` | ✅ | Pointer loop; declared in `<string.h>`. |
| `strncpy` | ❌ | Bounded `strcpy` |
| `strcat`, `strncat` | ❌ | Append to NUL-terminated dst |
| `strchr`, `strrchr` | ❌ | Find char in NUL-terminated string |
| `strstr` | ❌ | Find substring |
| `strtok_r` (POSIX) / `strtok_s` (C11 Annex K) | ❌ | **The reentrant primitive — implement this first.** Takes an explicit `char **saveptr` so multiple concurrent calls don't clobber each other. dotcc should default to the reentrant shape across the libc surface (no hidden static state). |
| `strtok` | ❌ | C89 stateful form (static internal cursor → not thread-safe, can't be called recursively). When added, expose as a thin wrapper around `strtok_r` with a `[ThreadStatic]` saveptr — same shape as glibc, but documented as "use `strtok_r` if you can". |
| `strerror` | ❌ | Map errno to message |
| `memcmp` | ❌ | Trivial — `Span.SequenceCompareTo` |
| `memcpy` | ✅ | `Buffer.MemoryCopy`; declared in `<string.h>`. |
| `memmove` | ❌ | Same as memcpy but overlap-safe; `Buffer.MemoryCopy` is overlap-safe so add as an alias |
| `memset` | ✅ | `NativeMemory.Fill`; declared in `<string.h>`. |
| `memchr` | ❌ | Find byte in buffer |

### `stdint.h` (C99)

Synthetic header at `DotCC.Lib/include/stdint.h` declares the fixed-width integer typedefs + limit macros. Each typedef lowers via the standard dotcc typedef path to a C# `using unsafe NAME = T;` alias at file scope. Fixture `stdint-fixed-widths/`.

| Type | Status | Underlying C type → C# type |
|---|---|---|
| `int8_t` / `uint8_t` | ✅ | `signed char` / `unsigned char` → `sbyte` / `byte` |
| `int16_t` / `uint16_t` | ✅ | `short` / `unsigned short` → `short` / `ushort` |
| `int32_t` / `uint32_t` | ✅ | `int` / `unsigned int` → `int` / `uint` |
| `int64_t` / `uint64_t` | ✅ | `long` / `unsigned long` → `long` / `ulong` |
| `intptr_t` / `uintptr_t` | ✅ | `long` / `unsigned long` (LP64-style — dotcc's `long` is unconditionally 64-bit). Differs from MSVC-Windows's LLP64 where these are still 8 bytes but via `__int64`. Same observable behavior. |
| `size_t` / `ptrdiff_t` | ✅ | `unsigned long` / `long`. |
| `intmax_t` / `uintmax_t` | ✅ | `long` / `unsigned long`. |
| `INT8_MIN` / `MAX`, `UINT8_MAX`, …, `INT64_MIN` / `MAX`, `UINT64_MAX` | ✅ | `#define` numeric literals — usable as integer constant expressions. |
| `INTPTR_MIN` / `MAX`, `UINTPTR_MAX`, `SIZE_MAX`, `PTRDIFF_MIN` / `MAX`, `INTMAX_MIN` / `MAX`, `UINTMAX_MAX` | ✅ | All alias the `INT64`/`UINT64` macros (LP64). |
| `int_least8_t` / `int_fast8_t` / etc. families | ❌ | C99-optional — rarely seen in modern code. Same shape as the fixed-width forms above when added. |
| Format-string macros `PRId32` etc. | ❌ | Live in `<inttypes.h>` — separate header, not yet shipped. |

### `limits.h`

Synthetic header at `DotCC.Lib/include/limits.h` defines the C99 numeric-limit macros for the primitive int family. Pure `#define` numeric literals — usable as integer constant expressions. Fixture `limits-constants/`.

| Macro | Value | Notes |
|---|---|---|
| `CHAR_BIT` | 8 | bits per char (always 8 on dotcc; C# `byte`) |
| `MB_LEN_MAX` | 1 | UTF-8 per-byte model — each `char` is one byte |
| `SCHAR_MIN`/`MAX`, `UCHAR_MAX` | -128, 127, 255 | signed/unsigned `char` extrema |
| `CHAR_MIN`/`CHAR_MAX` | 0, 255 | plain `char` (dotcc unsigned). **Documented divergence**: MSVC's plain `char` is signed; programs that compare against CHAR_MIN/MAX produce different results across dotcc and MSVC. The signed/unsigned-explicit forms above are portable. |
| `SHRT_MIN`/`MAX`, `USHRT_MAX` | ±32768, 65535 | always 16-bit on both compilers |
| `INT_MIN`/`MAX`, `UINT_MAX` | ±2147483648, 4294967295u | always 32-bit |
| `LONG_MIN`/`MAX`, `ULONG_MAX` | ±9223372036854775808, 18446744073709551615uL | 64-bit on dotcc (LP64); 32-bit on MSVC-Windows (LLP64). Documented divergence — programs hard-depending on `LONG_MAX == 2147483647` behave differently. |
| `LLONG_MIN`/`MAX`, `ULLONG_MAX` | same as `LONG_*` | dotcc's `long long` == `long` (both 64-bit) |

### `float.h` (C99)

Synthetic header at `DotCC.Lib/include/float.h` exposes the IEEE-754 limit macros for `float` (binary32) and `double` (binary64). dotcc's `long double` *is* `double` (both lower to C# `double`), so the `LDBL_*` macros alias the `DBL_*` values (matches what real C does on platforms without extended precision). Fixture `float-limits/`.

| Macro | Value | Notes |
|---|---|---|
| `FLT_RADIX` | 2 | IEEE-754 base |
| `FLT_MANT_DIG` / `DBL_MANT_DIG` / `LDBL_MANT_DIG` | 24 / 53 / 53 | significand bits |
| `FLT_DIG` / `DBL_DIG` / `LDBL_DIG` | 6 / 15 / 15 | round-trip-safe decimal digits |
| `FLT_MIN_EXP` / `MAX_EXP` | -125 / 128 | binary-radix exponent bounds |
| `DBL_MIN_EXP` / `MAX_EXP` | -1021 / 1024 | likewise for double |
| `FLT_MIN_10_EXP` / `MAX_10_EXP` | -37 / 38 | decimal-radix exponent bounds |
| `DBL_MIN_10_EXP` / `MAX_10_EXP` | -307 / 308 | likewise for double |
| `FLT_MAX` / `DBL_MAX` / `LDBL_MAX` | IEEE-754 maxima | 3.402…e+38F / 1.797…e+308 |
| `FLT_MIN` / `DBL_MIN` / `LDBL_MIN` | smallest positive normalized | 1.175…e-38F / 2.225…e-308 |
| `FLT_EPSILON` / `DBL_EPSILON` / `LDBL_EPSILON` | gap to 1.0 | 1.192…e-7F / 2.220…e-16 |
| `FLT_ROUNDS` | 1 | round to nearest (IEEE-754 + .NET FP default) |
| `FLT_EVAL_METHOD` | 0 | no extra precision; each op evaluates in its declared type |

### `math.h`

Every function exists as a `double` overload (routes to `System.Math`) **and** a `float` overload (routes to `System.MathF`); the explicit `…f`-suffix C99 forms (`sinf`, `cosf`, `sqrtf`, …) are wired alongside. Lives in `DotCC.Libc/MathLib.cs` as a `partial` extension of `Libc`. The same `.cs` file is **embedded into `DotCC.Lib.dll`** at build time (`<EmbeddedResource Include="..\DotCC.Libc\*.cs">`); `Compiler.LoadRuntimeBlock` splices it into every emitted program's type-decls section, and `using static Libc;` brings the methods into scope by bare name so user calls resolve through C# overload resolution. Single source of truth — no duplicated inline copy. Header lives at `DotCC.Lib/include/math.h` (also embedded — see "Synthetic system headers + runtime" below). Fixture `math-basic/` covers double-precision dispatch end-to-end with MSVC oracle validation.

| Function | Status | Notes |
|---|---|---|
| `sin`, `cos`, `tan` (+ `…f`) | ✅ | `Math.Sin` / `MathF.Sin` etc. |
| `asin`, `acos`, `atan`, `atan2` (+ `…f`) | ✅ | `Math.Asin` / `MathF.Asin` etc. |
| `sinh`, `cosh`, `tanh` (+ `…f`, C99) | ✅ | `Math.Sinh` / `MathF.Sinh` etc. |
| `exp`, `log`, `log10`, `log2` (+ `…f`, C99) | ✅ | `Math.Exp` / `MathF.Exp` etc. |
| `pow`, `sqrt`, `cbrt` (+ `…f`, C99) | ✅ | `Math.Pow` / `Sqrt` / `Cbrt` and MathF counterparts |
| `ceil`, `floor`, `round`, `trunc` (+ `…f`, C99) | ✅ | `round` forced to `MidpointRounding.AwayFromZero` to match C99 (BCL default is banker's rounding, which diverges from MSVC). |
| `fabs`, `fmod`, `fmin`, `fmax` (+ `…f`, C99) | ✅ | `Math.Abs` / native `%` for fmod (sign-of-x matches C99) / `Math.Min` / `Math.Max` |
| `NAN`, `INFINITY`, `HUGE_VAL`, `HUGE_VALF` | ✅ | Properties on `Libc` returning `double.NaN` / `double.PositiveInfinity`. |
| `M_PI`, `M_E`, `M_SQRT2`, `M_LN2`, `M_LN10` | ✅ | `const double` fields on `Libc`. dotcc defines them unconditionally; MSVC gates them behind `_USE_MATH_DEFINES` — portable code defines that macro before `#include <math.h>`. |
| `isnan`, `isinf`, `isfinite` (C99) | ✅ | Return `int` (1 / 0) — match C99 macro semantics, not C# `bool`. Both float and double overloads. |
| Type-generic `tgmath.h` (C99) | ✅ | C# already has overload resolution, so we sidestep C11 `_Generic` entirely: the float + double overloads on `Libc` give type-generic dispatch for free. `tgmath.h` is therefore just a one-line wrapper that `#include`s `math.h`. Fixture `tgmath-overload/`. |

### `ctype.h`

Synthetic header + implementations in `DotCC.Libc/CTypeLib.cs`. All predicates implement the "C" locale behavior — ASCII only, bytes outside 0..127 return 0 (matches glibc with `LC_ALL=C`). Predicates take `int` and return `int` (non-zero on match, zero otherwise) per the standard. Fixture `ctype-predicates/`.

| Function | Status | Notes |
|---|---|---|
| `isalpha`, `isdigit`, `isalnum` | ✅ | A-Z / a-z, 0-9, union of both. |
| `isspace` | ✅ | ` \t\n\r\v\f` |
| `isupper`, `islower` | ✅ | A-Z, a-z |
| `isxdigit` | ✅ | 0-9 / A-F / a-f (hex chars) |
| `iscntrl`, `isprint`, `isgraph` | ✅ | < 0x20 \|\| == 0x7F  /  0x20..0x7E  /  0x21..0x7E |
| `ispunct` | ✅ | `isgraph && !isalnum` |
| `toupper`, `tolower` | ✅ | ASCII letter mapping; non-letter / non-ASCII passes through unchanged (returns `int` so EOF round-trips). |

### `time.h`

| Function | Status | Notes |
|---|---|---|
| `time` | ❌ | `DateTimeOffset.UtcNow.ToUnixTimeSeconds` |
| `clock`, `CLOCKS_PER_SEC` | ❌ | `Environment.TickCount64` or `Stopwatch` |
| `difftime` | ❌ | Subtraction |
| `localtime`, `gmtime`, `mktime`, `strftime`, `asctime`, `ctime` | ❌ | DateTime mapping is non-trivial; lower priority |

### `assert.h`

Synthetic header at `DotCC.Lib/include/assert.h` with the canonical `NDEBUG`-aware shape. Implementations in `DotCC.Libc/AssertLib.cs`. Fixture `assert-basic/` covers the happy path; unit tests cover the failure throw + NDEBUG no-op.

| Function | Status | Notes |
|---|---|---|
| `assert(expr)` | ✅ | When `NDEBUG` is undefined, expands to `__dotcc_assert(expr)` — overloaded for `int` / `bool` / `double` / `void*` so any truthy expression resolves through C# overload resolution. Failed assertion throws `Libc.AssertionFailedException` carrying the source text of `expr` (via `[CallerArgumentExpression]` at the call site — gives glibc-style diagnostic quality without preprocessor stringification). When `NDEBUG` IS defined, expands to a call to `__dotcc_assert_noop()` so the expression is NOT evaluated (matches C99 §7.2.1.1; rare side-effecting `assert(f())` style won't run `f`). Re-includable: `#undef` at the top of the header lets the macro be redefined according to current `NDEBUG` state on every `#include`. |
| `_Static_assert` / `static_assert` (C11/C23) | 🟡 | Parsed at file scope and block scope, both the C11 two-arg `_Static_assert(expr, "msg")` and the C23 message-less `static_assert(expr)` arities. `_Static_assert` is always a keyword; lowercase `static_assert` is promoted onto it by `DialectKeywordRewriter` under `-std=c23` (pre-C23 it's an ordinary identifier — `static_assert(...)` then parses as a function call). **Compile-time only and currently dropped, not evaluated** — dotcc has no constant-expression evaluator yet, so the assertion lowers to an inert block comment. Observably identical to clang for every *valid* program (a passing static_assert emits nothing either way); a *false* static_assert that clang would reject is silently accepted — the one gap, pending a const-eval pass. Fixture `c23-static-assert/`. |

### `errno.h`, `signal.h`, `setjmp.h`

| Header | Status | Notes |
|---|---|---|
| `errno.h` (errno, perror) | 🟡 | Thread-local int; map common values to BCL exceptions |
| `signal.h` | 🚫 | Out of scope — managed runtimes don't model POSIX signals usefully |
| `setjmp.h` (`setjmp`/`longjmp`) | 🟡 | Implemented via .NET exceptions. `longjmp(env, val)` throws a tagged `LongJmpException` carrying the env token + value; the emitter recognises `if (setjmp(env) == 0) {normal} else {recovery}` and `if (setjmp(env)) {recovery} else {normal}` patterns in user code and rewrites the `if/else` into `try / catch when (__jmp.Token == env)`. **Bonus over real C**: `finally` blocks DO run on the unwind (.NET exception semantics) — strictly better than real `longjmp`'s silent-skip behavior. **Limitation**: only the two recognised `if/else` shapes work. Other forms (`switch (setjmp(env))`, raw value capture into a variable, `setjmp` outside `if/else` conditions) raise `CompileException`. `jmp_buf` lowers to the predefined C# type `Libc.LongJmpToken` via the TypeNameRewriter seed list — no fake keywords in the grammar. Fixture `setjmp-cleanup/` exercises a 3-deep frame unwind. |

## Beyond C99

| Feature | C version | Status | Notes |
|---|---|---|---|
| Variadic macros, mixed decls/code, designated init, `restrict`, `_Bool`, `__func__`, line comments | C99 | various above | dotcc's baseline target (VLAs 🚫, complex types / `inline` not yet — see rows above) |
| `_Generic` | C11 | ❌ | Type-generic dispatch. Lower priority than it'd otherwise be: the most common use case (`tgmath.h`) is already covered by C#'s overload resolution on `Libc` — see the `tgmath.h` row above. |
| `_Static_assert` / `static_assert` | C11 | 🟡 | Parsed (file + block scope, both arities; lowercase promoted under `-std=c23`) but dropped to an inert comment, not evaluated — see the dedicated row above. |
| `_Noreturn` / `noreturn` | C11 | ❌ | Cosmetic — emit `[DoesNotReturn]` |
| Anonymous structs/unions | C11 | ❌ | Depends on `struct`/`union` |
| `_Thread_local` / `thread_local` | C11 | ❌ | Lower to `[ThreadStatic]` |
| `_Alignas`, `_Alignof` | C11 | ❌ | `[StructLayout(Pack=N)]` |
| Bounds-checked interfaces (Annex K) | C11 | 🚫 | Almost no real C uses these |
| `threads.h` | C11 | 🟡 | Mapped onto .NET `System.Threading` (`DotCC.Libc/ThreadsLib.cs`). **v1 subset:** `thrd_create` / `thrd_join` / `thrd_yield`; `mtx_init` / `mtx_lock` / `mtx_trylock` / `mtx_unlock` / `mtx_destroy` (plain + recursive; timed acts as plain). `thrd_t` / `mtx_t` are blittable handle structs (seeded type names → `Libc.thrd_t` / `Libc.mtx_t`) so `thrd_t t; thrd_create(&t,…)` works; `thrd_start_t` lowers to `delegate*<void*,int>` and `&func` to the matching function pointer. A thread's `int` result is stashed and returned by `thrd_join` (a .NET `Thread` has no return channel). **Deferred:** `cnd_*` (condvars — .NET's `Monitor.Wait/Pulse` is bound to the lock object, not a standalone `cnd_t`), `tss_*` (no per-thread-exit TLS destructor in .NET), `call_once`, `thrd_current`/`thrd_equal`/`thrd_sleep`/`thrd_detach`/`thrd_exit`, `mtx_timedlock`. dotcc does **not** define `__STDC_NO_THREADS__`. Fixture `threads-counter/` (4 threads × 10000 increments under a mutex → 40000; gcc oracle built with `-pthread`). |
| `nullptr` / `true` / `false` constants | C23 | ✅ | First-class keyword constants under `-std=c23` (no `<stdbool.h>`/`<stddef.h>`), lowering to C# `null` and the integer literals `1`/`0` (normalized through `CBool`). Promoted by `DialectKeywordRewriter`; pre-C23 they stay identifiers. See the type table above. |
| `constexpr` | C23 | ❌ | Compile-time evaluation — useful for `enum`/`switch` |
| `typeof`, `typeof_unqual` | C23 | ❌ | Type inference — useful for macros |
| `[[attribute]]` syntax | C23 | ❌ | Map common ones (`[[noreturn]]`, `[[deprecated]]`) to `[DoesNotReturn]` etc. |
| `_Float128` / `__float128` | C23 | ✅ | IEEE-754 binary128 via the MIT software `DotCC.Libc.Float128` (oracle-validated vs gcc). See the type table above. |
| `_BitInt(N)` | C23 | 🚫 | No C# equivalent; .NET 7+ `Int128`/`UInt128` only |
| Empty initializer `{}` | C23 | ❌ | Brace-init with no elements: `int a[] = {};`, `struct S s = {};`, `T x = {}` (zero-init). **Standard as of C23** (later standards supersede earlier ones — it's real C now, not just a GNU extension). dotcc's `InitList`/`MemberInitList` require ≥ 1 element, so `{}` is a parse error. Would need empty-list productions (`InitList → ` / a dedicated zero-init form) plus a C23 dialect gate. |
| `#embed` | C23 | ❌ | Embed file bytes — could lower to `byte[]` literal |
| `auto` (type inference) | C23 | ✅ | `auto x = E;` deduces x's type from the initializer — exactly C# `var` (= C++ `auto` = gcc's older `__auto_type`), so dotcc lowers it straight to `var x = E;` and lets Roslyn infer. Context-disambiguated from the pre-C23 storage-class meaning (`auto int x;`, dropped) the same way gcc/clang do: after `auto`, a plain `ID` → inference, a type → storage class. Gated as C23 under `-pedantic`. Fixture `auto-infer/`. |

## Remaining syntax gaps, by dialect

A consolidated quick-reference of C *syntax* (grammar) not yet supported, grouped
by the standard that introduced it. The detailed per-feature rows above are the
source of truth; this is the at-a-glance roadmap. **Severity:** ⛔ parse/lex
error (fails loudly — safe, never a wrong answer) · 🟡 partial · ❌ roadmap
(would-implement) · 🚫 deliberately out of scope. **There are no known silent
miscompiles** — every gap below fails at parse/lex time or is explicitly flagged.

**C89 / C90**
- ⛔ Pointer-to-array declarator `int (*p)[3]` (awkward in the flattened-array model).
- ⛔ Standalone function-pointer *parameter* `void qsort(…, int (*cmp)(…))` — use the `typedef`'d form (the standalone *local* declarator IS supported).
- 🚫 K&R function definitions; `register` storage class. (`auto` is supported — storage-class form dropped, C23 inference form → `var`.)

**C99**
- ⛔ Compound literals `(int[]){1,2,3}`, `(struct S){…}`.
- ⛔ Array designators `[2] = 5` (struct designators `.x = 5` work).
- ⛔ Hex float literals `0x1.8p3`.
- ⛔ `_Complex` / `_Imaginary`.
- ⛔ Flexible array members `struct { int n; int data[]; }`.
- ⛔ Nested-brace multi-dim initializers `int m[2][2] = {{1,2},{3,4}}` (bare multi-dim decl + access work).
- 🚫 VLAs — out of scope (rare, optional since C11, managed-runtime mismatch). A 1-D runtime extent incidentally lowers to `stackalloc`, but it's not a pursued feature.
- ❌ `inline` (cosmetic — could emit `[MethodImpl(AggressiveInlining)]`).

**C11**
- ⛔ `_Generic` (generic selection).
- ⛔ Anonymous `struct` / `union` members.
- ⛔ `_Alignas` / `_Alignof`, `_Atomic`, `_Noreturn`, `_Thread_local`.
- ⛔ String/char encoding prefixes `u"…"` / `U"…"` / `u8"…"` / `L"…"` (dotcc is UTF-8-native; the prefix syntax isn't parsed).

**C23**
- ⛔ `_BitInt(N)`, `typeof` / `typeof_unqual`, `constexpr`.
- ⛔ `[[attributes]]` (`[[nodiscard]]`, `[[maybe_unused]]`, …).
- ⛔ Empty initializer `{}`.
- ⛔ `#elifdef` / `#elifndef`, `#embed`.

**Cross-dialect**
- ⛔ String literal with a high-byte escape `"\xff"` / `"\377"` (> 0x7F can't be one byte in a C# `u8` literal — fails loudly rather than miscompile).
- ⛔ Float literal suffix `1.5L` (the `long double` *type* works; only the literal suffix doesn't lex).

## Out of scope (won't implement)

Listed here so we don't relitigate them. All marked 🚫 above.

- **Variable-length arrays (VLAs)** — rarely used, made optional in C11, and a managed-runtime mismatch (unbounded stack growth). A 1-D runtime extent incidentally lowers to `stackalloc`, but full VLA support isn't pursued.
- **Trigraphs / digraphs** — removed in C23, never useful.
- **Wide string/char literals** (`L"…"`, `wchar_t`, `wprintf`) — dotcc is UTF-8-native; wide types add no value and complicate the lowering.
- **`volatile`** — no useful semantic on managed runtimes; we just accept and ignore the qualifier.
- **`signal.h`** — POSIX signal model doesn't map onto .NET.
- **`gets`** — removed in C11; security disaster.
- **Annex K bounds-checked interfaces** — almost no real-world C uses them.
- **`_BitInt(N)`** — only well-defined for two widths on .NET; not worth the complexity.

## Synthetic system headers + runtime

dotcc ships its own copies of the C99/C11 standard headers (`stdio.h`, `stdlib.h`, `stddef.h`, `stdbool.h`, `stdint.h`, `limits.h`, `float.h`, `assert.h`, `ctype.h`, `setjmp.h`, `math.h`, `tgmath.h`, `string.h`, `threads.h`) AND its libc implementations, both as **real files in source control**, both embedded into `DotCC.Lib.dll` so the compiler can serve them at emit time without any runtime disk I/O. Same model as clang's `lib/clang/<ver>/include/` tree, just loaded from the assembly manifest.

**Two parallel embeddings (see `DotCC.Lib.csproj`):**

| Source location | Logical name in manifest | Loader | Consumed by |
|---|---|---|---|
| `DotCC.Lib/include/*.h` | `DotCC.SystemHeaders.<filename>` | `Compiler.LoadEmbeddedSystemHeaders` | Preprocessor: resolves `#include <math.h>` etc. against the embedded files |
| `..\DotCC.Libc\*.cs` | `DotCC.Runtime.<filename>` | `Compiler.LoadRuntimeBlock` | `BuildShell`: splices the runtime source into the emitted program's type-decls section |

The runtime embedding is what makes dotcc's emit **single-source-of-truth**. The `.cs` files under `DotCC.Libc/` (`Libc.cs`, `MathLib.cs`, `PrintfBuilder.cs`, `ScanfReader.cs`, `SprintfBuilder.cs`) compile into `DotCC.Libc.dll` for unit testing AND are loaded from the manifest at emit time. `LoadRuntimeBlock` strips file-scope artifacts (`#nullable enable`, `using` directives, `namespace DotCC.Libc;`) so the contained class declarations land cleanly at file scope in the emitted program. Every emitted file then has `using static Libc;` at the top, which brings `printf` / `malloc` / `sin` / `cos` / `sqrt` / etc. into scope by bare name.

**Header guards use the standard glibc/MSVC convention** (`_STDIO_H`, `_MATH_H`, …) rather than a dotcc-specific prefix, so portable code that does `#ifdef _MATH_H` to detect whether the header has been pulled in works correctly.

**Why this design**: emitted file-based programs (`#:property AllowUnsafeBlocks=true`) stay self-contained — you can `dotnet run hello.cs` with no `<PackageReference>` and no external assemblies — but the implementations live in real refactorable `.cs` files, not as raw-string literals in the compiler source. The migration path to a NuGet-distributed `DotCC.Libc` is a one-line change in `BuildShell` (drop the `{{runtimeBlock}}` splice, add `#:package DotCC.Libc@<ver>` to the file-based header).

**MSVC oracle interop**: when `cl.exe` compiles the same `.c` source, it uses its own `<math.h>` (not ours). Both compilers declare the same C99 surface, so the differential test still produces matching output. Each side using its own copy of the standard headers is by design, not a bug.

## Standards interop (autoconf / `./configure`)

A real C compiler has to be driveable by feature-detection tooling. The autoconf model is: `$CC -c conftest.c` runs against tiny probe programs, and the exit code drives `#define HAVE_X 1` macros in `config.h`. dotcc participates in this naturally because its CLI is clang-shaped (`-c`, `-E`, `-I`, `-D`, `-o`) and it exits non-zero on parse failure.

**Probes that work out of the box** (`CC=dotcc ./configure`):

| Probe shape | Outcome | Why |
|---|---|---|
| `AC_CHECK_HEADERS([stdio.h])` / `[stdlib.h]` / `[stddef.h]` / `[stdbool.h]` / `[math.h]` / `[tgmath.h]` | ✅ HAVE_X=1 | Resolved via embedded synthetic headers under `DotCC.Lib/include/`. |
| `AC_CHECK_FUNCS([printf])` / `[malloc]` / `[free]` / `[strlen]` / `[strcmp]` / `[strcpy]` / `[memset]` / `[memcpy]` / `[sin]` / `[cos]` / `[sqrt]` / `[pow]` / … | ✅ HAVE_X=1 | Declared in our synthetic headers; the probe links because `using static Libc;` makes them resolvable in the emitted C#. |
| `AC_CHECK_HEADERS([unistd.h])` / `[errno.h]` / `[string.h]` / `[time.h]` / `[ctype.h]` | ❌ HAVE_X=0 | Not (yet) embedded — autoconf will correctly route to fallback code paths. |
| `AC_CHECK_FUNCS([fopen])` / `[atoi]` / `[strchr]` / `[strstr]` / `[abs]` | ❌ HAVE_X=0 | Not declared yet — see the ❌ rows in the libc tables above. As features land, probes flip from ❌ to ✅ automatically. |

**Probes that need workarounds:**

- **Dialect flags** — `-std=c99`, `-pedantic`, and `-pedantic-errors` are recognized (`-pedantic`/`-pedantic-errors` drive the dialect-rejection gate). **Unknown flags (`-Wall`, `-O2`, `-g`, `-fPIC`, `-march=native`, …) are now accepted-and-ignored** (warned once, partitioned out of the input-file list) rather than erroring — so a `./configure`/`make` driving dotcc survives the gcc/clang flag grab-bag. The one residual case: a space-separated unknown flag that consumes a following token (`-march native` rather than `-march=native`) would mis-read `native` as an input file; the `=`-joined forms are fine.
- **Compiler identity macros** — `__GNUC__` / `__clang__` / `_MSC_VER` aren't predefined. Codebases probing for these correctly land in the generic-compiler fallback, which is usually a safe default. We could predefine a `__DOTCC__` macro so codebases can target dotcc-specific paths if needed.
- **`AC_CHECK_LIB([m], [sin])`** — link-time probes. dotcc doesn't produce `.o`/`.a` and doesn't accept `-l`. Workaround: declare a synthetic libm and have the probe pass at compile-time even without real link semantics. The `sin` function is already in our embedded runtime so a no-op `-l` accept-and-ignore would work.
- **`AC_RUN_IFELSE`** — probes that compile AND execute the probe program. Works in principle (emitted `.cs` runs via `dotnet run`), but autoconf expects a native executable in `./conftest.exe`. A `dotcc --wrap` mode that emits a shim shell script invoking `dotnet run` on the emit would close this gap. Lower priority — most probes are compile-only.
- **Error-message-parsing probes** — a few legacy probes grep gcc-style diagnostics (`"error: implicit declaration"` etc.). dotcc emits its own format (`"dotcc: parse failed: …"`). Most modern probes check only exit codes; the legacy ones need either a `--diag-format=gcc` shim or codebase-level patches.

**Realistic next milestone**: pick a small autotools-using codebase (`libcsv`, a small zlib subset, a stb_*-style single-file thing) and run `CC=dotcc ./configure && make`. The first failure tells you where to invest. Most likely landing-pad: unrecognized dialect/warning flags — a 5-minute fix in `System.CommandLine`.

## Test corpus and oracles

**dotcc-managed fixtures** live in `DotCC.FunctionalTests/Fixtures/<name>/`. Each is a small idiomatic C program with a hand-written `expected-stdout.txt`. They're the smoke layer — they exercise a specific feature or interaction (`loop-break`, `fizzbuzz`, `fibonacci`, etc.) and grow as the language does. Adding one is mechanical: drop a folder, the theory test picks it up.

**MSVC oracle**: `DotCC.FunctionalTests/MsvcOracle.cs` finds Microsoft's C compiler via `vswhere.exe` → `vcvars64.bat` → `cl.exe`. When MSVC is available, `MsvcOracleTests` recompiles every fixture with cl and asserts that dotcc's stdout matches MSVC's byte-for-byte. This is **differential testing** — it catches dotcc emitter / libc divergences from real-C semantics that a hand-written expected file might miss (printf precision quirks, integer overflow, evaluation order). Skipped automatically when MSVC isn't on the host (non-Windows or no VS install). Adding a new fixture means it gets validated against MSVC for free — no per-fixture wiring.

**External corpora we could pull from (not yet)**:

- **GCC testsuite** — has a [torture-tests](https://gcc.gnu.org/onlinedocs/gccint/Torture-Tests.html) section that's specifically small, self-contained, compiler-stress-testing. License: GPL — vendoring needs care, but referencing for inspiration is fine.
- **tcc-test / lcc-test** — small compilers ship their own focused test suites; useful as a reference for what "MVP C compiler tests" look like.
- **`nothings/single_file_libs`** (Sean Barrett's stb_*) — these are real-world idiomatic C programs that intentionally stay simple. Compiling one of them end-to-end would be a meaningful milestone.
- **csmith** — random C program generator; useful once dotcc handles most of C99, for fuzz-testing.
- **Rosetta Code C entries** — varied small idiomatic programs across hundreds of tasks. Low-overhead source of new fixtures.

The right strategy is probably to grow the fixture corpus organically as features land (one fixture per feature, oracled by MSVC), and reach for external suites once the grammar covers most of C99.

## How to add a feature

1. **Grammar change**: edit `DotCC.Lib/c.lalr.yaml`. Add symbols (append; don't reorder existing entries), productions (with new `action: foo` names), and lexer rules. The source generator emits `C.Foo` AST records on next build — your visitor will get a compile error pointing at the missing `Visit(C.Foo n)`.
2. **Visitor lowering**: add `Visit(C.Foo n) => …` in `DotCC.Lib/CSharpEmitter.cs`. Aim to emit syntactically clean C# that preserves C's observable semantics; reuse Libc helpers when available (`Printf`, `Malloc`, etc.).
3. **Libc surface**: if the feature needs a new runtime function (e.g. `strtol`), add it to `DotCC.Libc/Libc.cs` (routing to the obvious BCL primitive), plus a unit test in `DotCC.Tests/LibcTests.cs`.
4. **Fixture**: drop a `DotCC.FunctionalTests/Fixtures/<feature>/main.c` + `expected-stdout.txt`. The theory test discovers it automatically — no code change. Pick a fixture name that's descriptive (`loop-break`, `bitwise-mask`, `struct-point`).
5. **Dialect gate** (if dialect-sensitive): if the feature is newer than `c90` (or was *removed* by a standard), add a `DialectGate` call so `-pedantic` rejects it under an older `-std=` — see `DotCC.Lib/DialectGate.cs`. Introduced-in features call `Gate(era, …)` / `RequireMin` at the natural layer (visitor, `TypeFromSpec`, `CPreprocessor`). **The currently-moot features will need this when they become parseable: the *removed* ones — K&R definitions and trigraphs (both removed C23) — which need a removed-in / `RequireMax` direction that `DialectGate` does not have yet (add it when the first one lands). (VLAs are 🚫 out of scope, so no gate needed.)** Add unit tests in the dialect-gating region of `DotCC.Tests/CompilerTests.cs`.
6. **Update this file**: flip the row from ❌ to ✅, mention the fixture in **Notes**.

The grammar conventions (precedence ladder, dangling-else handling, lexer ordering) live in `CLAUDE.md` under **Grammar conventions** — read that before touching `c.lalr.yaml`.
