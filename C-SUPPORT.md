# C language support in dotcc

Running tracker of what dotcc's grammar and libc cover today. Update this when a feature lands (add the fixture / test reference in **Notes**), when one moves from ❌ to 🟡 to ✅, or when something is decided out of scope (🚫 with reason).

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
| Identifiers `[a-zA-Z_][a-zA-Z_0-9]*` | ✅ | `ID` token |
| Decimal int literal | ✅ | `NUM` token |
| Hex int literal `0xFF` | ✅ | Lexer rule above NUM (longest-match wins); visitor passes through — C# accepts identical syntax. Fixture `bitwise/` |
| Octal int literal `0755` | ❌ | Conflicts with `0` then `755`; add rule, longer-match wins |
| Binary int literal `0b1010` | ❌ | C23 — straightforward addition |
| Int suffixes `123u`, `123L`, `123ull` | ❌ | Lex rule + visitor map (`L` → `long`, `u` → `uint`) |
| Float literal `1.5`, `1.5e10`, `1.5f` | ✅ | `FLOAT` token (mandatory `.` to disambiguate from `NUM`) |
| Double suffix `1.5l` (long double) | 🚫 | C# has no `long double`; we pass `f`/`F` through, others won't typecheck |
| Char literal `'a'`, `'\n'` | ✅ | `CHAR` lexer rule split into two (escape-form + plain) to avoid IRx alternation; visitor lowers to `(byte)'X'`. Supported escapes: `\n \r \t \\ \' \" \0 \b \f`. Fixture `small-ops/` |
| String literal `"…"` | ✅ | `STRING` token; lowered to `L("…\0"u8)` |
| Wide string literal `L"…"`, `u"…"`, `U"…"` | 🚫 | dotcc is UTF-8-native — wide types add no value |
| Escape sequences `\n \t \\ \" \xNN` | 🟡 | Passed verbatim into the C# UTF-8 literal; C# accepts most; `\x` accepted in C and C# alike |
| Trigraphs `??=` etc. | 🚫 | Removed in C23; never useful |
| Digraphs `<% %> :> :>` etc. | 🚫 | Same reasoning |

## Types

| Feature | Status | Notes |
|---|---|---|
| `int` | ✅ | Lowered to C# `int` |
| `char` | ✅ | Lowered to C# `byte` (so `char*` arithmetic walks bytes) |
| `short`, `unsigned short` | ❌ | Lower to `short` / `ushort` |
| `long`, `long long`, `unsigned long`, `unsigned long long` | ❌ | Lower to `long` / `ulong` (32-vs-64 bit char/short mismatch is documented as dotcc-quirk) |
| `signed` / `unsigned` qualifiers | ❌ | Map to C# signed/unsigned int variants |
| `float` | ✅ | C# `float` |
| `double` | ✅ | C# `double` |
| `long double` | 🚫 | No C# equivalent |
| `void` | ✅ | C# `void`; only valid as return type or `void*` |
| `_Bool` / `bool` (C99 stdbool.h) | ❌ | Lower to C# `bool`; libc shim needs `stdbool.h` synth header |
| Pointer types `T*`, `T**`, … | ✅ | Composes left-to-right via `TypePtr` production |
| Array decl `T arr[N]` | ✅ | Lowered to `T* arr = stackalloc T[N]` so block-scoped automatic arrays match C's lifetime + subscript semantics; fixture `array-sum/` |
| Array param decay `T arr[]` | ❌ | Decay to `T*` (same as C) |
| Variable-length arrays (C99) | ❌ | C99 optional; deprioritised — most idiomatic C code uses `malloc` |
| `struct` declaration | ✅ | Lowered to `unsafe struct ID { public T field; … }`; fields public to match C accessibility. Emitted via a visitor side channel into the type-decl section (C# requires types after top-level statements). Fixture `struct-point/` |
| `struct` member access `.` / `->` | ✅ | Both lowered verbatim; C# accepts `->` on struct pointers in unsafe context (identical semantics to C). Fixture `struct-point/` covers both forms |
| `struct ID` as a type | ✅ | `Type → 'struct' ID` lowers to bare `ID` in C# usage position (C# doesn't require `struct` prefix outside the declaration) |
| Forward struct decl `struct Node;` | ✅ | `Fn → 'struct' ID ';'` emits nothing in C# (types hoist regardless of declaration order). Needed for self-referential types where the pointer use precedes the full definition in a header. |
| Self-referential structs (linked list / tree) | ✅ | Works without special handling — `struct Node { struct Node* next; }` lowers cleanly because `Type → 'struct' ID` emits just the name and the resulting `Node* next` field references the struct being defined. Fixture `struct-linked-list/`. |
| Struct return-by-value | ✅ | `T f() { T x; …; return x; }` works — C# structs are value types, copy-on-return matches C semantics. Fixture `struct-linked-list/` (uses `make_pair` returning a struct). |
| `sizeof(Type)` | ✅ | Lowered verbatim to C# `sizeof(T)`; valid in unsafe context for any unmanaged type (which dotcc's structs all are). Fixture `struct-point/` |
| `sizeof expr` (expression form) | ❌ | Needs type inference to lower correctly — defer to whenever a type system shows up |
| `union` | ✅ | Lowered to C# struct with `[StructLayout(LayoutKind.Explicit)]` + `[FieldOffset(0)]` on each member — matches C's overlapping-storage semantics. Plain non-init `union U u;` becomes `union U u = default;` so C#'s definite-assignment is satisfied (overlapping fields can't be written individually). Fixture `union-bits/` |
| `enum` | ✅ | Lowered to `static class EnumName { public const int Member = VAL; … }` in the type-decl section. The emitter tracks each enumerator → enum-name in a side map (`_enumerators`) and rewrites unqualified `Member` references to `EnumName.Member` at the `Var` visitor — so user code keeps writing bare `Red` and the C# lands as `Color.Red`. `const int` (not real C# `enum`) keeps the value usable directly as an int in printf, arithmetic, and switch cases without casts. Auto-numbering: each item without an explicit value takes `previous + 1`. `enum Name` as a type ref → plain `int`. Fixture `enum-day/` |
| `typedef` | ✅ | `TypeNameRewriter` (a `RewritingTokenStream` slotted after the preprocessor) implements the classic "lexer hack" — promotes `ID` → `TYPE_NAME` for any name previously bound by `typedef`. Productions: `Fn → typedef Type ID ;` (simple alias, including `typedef struct Foo Foo;`) and `Fn → typedef struct ID { MemberList } ID ;` (struct def + alias). Emitter lowers simple aliases to C# `using unsafe Alias = Underlying;` at file scope; struct-with-alias becomes a single `unsafe struct Alias { … }`. Fixture `typedef-point/` |
| `const T` qualifier | 🟡 | Recognized by C compilers — dotcc currently ignores |
| `volatile T` | 🚫 | No useful C# equivalent for our targets |
| `restrict T` (C99) | 🚫 | Optimisation hint — no equivalent |
| Function pointer `T (*fp)(args)` | 🟡 | Supported via `typedef int (*Name)(args);` — lowers to `using unsafe Name = delegate*<args, int>;`. The standalone form `int (*fn)(int)` is famously hard to LALR-parse (embedded name); typedef is the idiomatic case. Param names required in the typedef body (`(int a, int b)` not `(int, int)`). `&function_name` becomes the function pointer constant via C#'s `&LocalFunction`. Fixture `fnptr-sort/` (bubble-sort with `Comparator` callback). |
| Compound literals `(int[]){1,2,3}` (C99) | ❌ | Depends on array support |

## Operators

| Feature | Status | Notes |
|---|---|---|
| Arithmetic `+ - * /` | ✅ | `Add`/`Mul` productions |
| Modulo `%` | ✅ | At `Mul` precedence; fixture `fizzbuzz/` |
| Unary `+ - *` (deref) `&` (addrof) | ✅ | `Unary` productions |
| Logical `&& \|\|` | ✅ | At `LAnd` / `LOr` precedence |
| Logical `!` (unary not) | ✅ | `Unary → '!' Unary` lowers to `(Cond.B(E) ? 0 : 1)` so the result is `int` (matching C's `!x` yielding 0 or 1, not bool). Fixture `small-ops/` |
| Comparison `< > <= >= == !=` | ✅ | `Rel` and `Equ` productions |
| Bitwise `& \| ^ << >>` | ✅ | `BOr` / `BXor` / `BAnd` / `Shift` non-terminals inserted at proper C precedence; fixture `bitwise/` |
| Bitwise `~` (unary) | ✅ | `bNot` action in `Unary`; fixture `bitwise/` |
| Assignment `=` | ✅ | Right-associative via `rightmost` precedence group |
| Compound assign `+= -= *= /= %=` | ✅ | In the rightmost group with `=`; fixture `factorial-for/` uses `*=`, `fibonacci/` uses `+=` via local var |
| Compound assign `&= \|= ^= <<= >>=` | ✅ | All five in the rightmost-= group; fixture `bitwise/` covers `\|=`, `^=`, `<<=`, `>>=` |
| Increment / decrement `++` / `--` (pre and post) | ✅ | `preInc`/`preDec` in `Unary`, `postInc`/`postDec` in `Postfix`; fixture `factorial-for/`, `fibonacci/`, `fizzbuzz/` (all use `i++`) |
| Ternary `c ? a : b` | ✅ | `E → LOr '?' E ':' E` in the rightmost E group. Lowers to `(Cond.B(c) ? a : b)`. Right-associative — chained ternaries `a ? 1 : b ? 2 : 3` work naturally. Fixture `small-ops/` |
| Comma operator `a, b` | ❌ | At top of E ladder, evaluates left, returns right |
| `sizeof(type)` and `sizeof expr` | ❌ | Type form is a `Unary`; expr form needs type inference (later) |
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
| Block `{ … }` | ✅ | `block` / `blockEmpty` |
| `if` / `if`–`else` | ✅ | Dangling-else resolved by `rightmost` precedence group |
| `while (e) stmt` | ✅ | `stmtWhile` |
| `do { … } while (e);` | ✅ | `Stmt → 'do' Stmt 'while' '(' E ')' ';'`. Cond wrapped with `Cond.B(...)` like other loops. Fixture `small-ops/` |
| `for (init; cond; incr) stmt` | ✅ | Two productions: `stmtForDecl` (decl-init) + `stmtForExpr` (expr-init); fixtures `factorial-for/`, `fibonacci/`, `fizzbuzz/` |
| `switch (e) { case … }` | ✅ | `CaseList` non-terminal owns the case chain; each `case`/`default` clause is `case E : StmtList` / `default : StmtList`. Emitted as C# switch verbatim — user writes `break;` per case (matches C convention and C# requirement); fixture `switch-day/` |
| `break;` | ✅ | `stmtBreak`; fixture `loop-break/` |
| `continue;` | ✅ | `stmtContinue`; fixture `loop-break/` |
| `return;`, `return e;` | ✅ | `stmtReturn` / `stmtReturnVoid` |
| `goto label;` + labels | ❌ | Add label-stmt and `goto` production; lower to C# `goto` literally |
| Empty stmt `;` | ❌ | Add `Stmt -> ';'` rule |

## Declarations

| Feature | Status | Notes |
|---|---|---|
| Function definition `T f(args) { … }` | ✅ | `funcDef` / `funcDefNoArgs` / `funcDefVoidArgs` (the C-correct `(void)` form). Static variants for each. |
| Function prototype `T f(args);` | ✅ | `protoDef` / `protoDefNoArgs` / `protoDefVoidArgs` — emitted as empty (C# methods hoist). Static variants for each. |
| Multiple declarators `int x, y;` | ✅ | `Decl → Type DeclItemList`; `DeclItem → ID | ID = E`. Plain item lowers to `name = default` (so C# definite-assignment is satisfied for struct/union fields). Mixed init / no-init works (`int a = 1, b, c = 3;`). Array declarators stay single-only (the lowering to `T* arr = stackalloc T[N]` doesn't compose with peers). Fixture `multi-decl/` |
| Initializer lists `int arr[] = {1, 2, 3}`, `Point p = {1, 2}` | ✅ | Array form lowers to `T* arr = stackalloc T[]{ … }` (both sized `int x[3]` and implicit-size `int x[]`). Struct positional init lowers to `Point p = new Point { x = 1, y = 2 };` — the emitter tracks field names per struct in `_structFields` (populated by `StructDef` / `TypedefStruct` / `UnionDef` draining the names pushed by each `StructMember` visit) and looks them up to rebuild the named-initializer form C# requires. Partial init `Vec3 v = {7}` zeroes the trailing fields (C# default semantics matching C). Fixtures `array-init/`, `struct-init/`. |
| Designated initialisers (C99) `{.x = 1}` | ✅ | New `MemberInit` / `MemberInitList` grammar productions; `Decl → Type ID '=' '{' MemberInitList '}'` distinguishes from the positional form by lookahead on `.` after `{`. User provides field names directly so no `_structFields` lookup is needed at emit time. Omitted fields zero-fill per C99 (matches C#'s struct object-initializer default behavior). Fixture `struct-linked-list/` (designated init for `Pair`). |
| Storage classes `static`, `extern`, `auto`, `register` | 🟡 | `static` recognized as a function-level modifier — function definitions with `static` have internal linkage (no `[UnmanagedCallersOnly]` export wrapper in library mode). `extern` not yet a keyword but bare top-level functions already have external linkage by C default. `auto`/`register` are 🚫. Block-scope `static`/`extern` on variables (file-scope statics, function-static locals) not yet. |
| `inline` (C99) | ❌ | Cosmetic — emit `[MethodImpl(AggressiveInlining)]` |
| Mixed decls and statements (C99) | ✅ | Already supported — `Stmt` accepts `Decl` |

## Preprocessor

| Feature | Status | Notes |
|---|---|---|
| `#include "header.h"` | ✅ | Quoted-form; resolves against `-I` dirs + `.c`'s own dir + synthetic `SystemHeaders` |
| `#include <header.h>` | ✅ | Angle-form supported by reassembling the fragmented `<`, name, `.`, ext, `>` tokens back into a filename — no lexer state change needed. Resolved against the same path stack as quoted-form (`-I` dirs + .c sibling dirs + synthetic SystemHeaders). |
| `#define NAME body` | 🟡 | Macro body stored as Items; substituted via `Rewrite` hook. Empty body OK (defined-as-marker) |
| `#define NAME(args) body` | ✅ | Function-like macros via `MacroExpander` (a `RewritingTokenStream` subclass — same pattern as `PreprocessorTokenStream` and `TypeNameRewriter`). Sits between the preprocessor and the typedef rewriter; uses `TryReadNext` lookahead to detect `(` after a macro name, paren-balanced arg collection (commas inside nested parens don't split), per-parameter substitution into the body, and a multi-pass rescan with a per-call hiding set so `#define CLAMP(x, lo, hi) MAX(lo, MIN(x, hi))` fully expands. Object-like still handled by `CPreprocessor.Rewrite` upstream. Fixture `macro-funclike/` (MSVC-oracle-validated). |
| `#undef NAME` | ✅ | `OnUndef` |
| `#if <expr>` | ✅ | Full C constant-expression evaluator (upstream in LALR.CC's `PreprocessorExpressionEvaluator`): integer literals (decimal `0x...` hex), `defined(NAME)` and `defined NAME`, arithmetic (`+ - * / %`), comparison (`< > <= >= == !=`), logical (`&& || !`), bitwise (`& | ^ ~ << >>`), ternary (`?:`), parens. Object-like macros expand before evaluation, so `#if VERSION >= 2` works when `VERSION` is `#define`'d. Fixture `preproc-cond/`. |
| `#ifdef NAME`, `#ifndef NAME` | ✅ | Built-in conditional engine consults `IsDefined`; fixture `hello/` uses `#ifndef` header guards |
| `#else`, `#elif`, `#endif` | ✅ | All three supported. `#elif` participates in the if/elif/else chain — the runtime branch stack tracks `(emitting, anyEmittedYet)` per entry, so once any arm is true the rest of the chain stays suppressed regardless of expression results. Fixture `preproc-cond/`. |
| `#error msg` | ✅ | Aborts compilation by throwing `CompileException` (same surface as a parse failure); frontend maps to a non-zero exit with `dotcc: #error: <msg>`. |
| `#warning msg` (C23) | ✅ | Emits `dotcc: #warning: <msg>` to stderr and continues. Same path as `#error` but non-fatal. |
| `#pragma …` | 🟡 | `#pragma once` is honored — `CPreprocessor` tracks `_pragmaOnceFiles` keyed by the currently-being-processed filename, short-circuits subsequent `#include` of the same name. All other pragmas silently ignored (matches the C convention: unknown pragmas don't break the build). |
| `#line N`, `#line N "file"` | 🚫 | Useful only for generated-code lineage which dotcc doesn't preserve |
| `##` token-pasting | ✅ | `MacroExpander.Substitute` recognizes `LHS ## RHS` patterns in function-like bodies: resolves each operand (formal-param → arg tokens, else literal token), pastes the last LHS token with the first RHS token into one ID-shaped token carrying the concatenated content. After paste, the multi-pass rescan re-checks the result — so `MAKEFOO(1)` → `foo_1` → resolves to the matching `#define foo_1 100`. Fixture `macro-ops/`. |
| `#` stringification | ✅ | `# PARAM` inside a function-like body emits a STRING token built from the arg's source text. Token contents joined with single spaces; `"` and `\` inside content are backslash-escaped so the resulting literal is well-formed. Fixture `macro-ops/`. |
| `__FILE__`, `__LINE__`, `__func__` (C99) | ✅ | `__FILE__` + `__LINE__` are preprocessor-time: `CPreprocessor.Rewrite` synthesizes a STRING token (containing the active filename, set per translation unit via `SetActiveFilename`, also tracked through nested `#include`s) and a NUM token (from the use site's `Position.Line`), respectively. `__func__` is visitor-time: `Visit(Var)` emits a unique placeholder, then each `Visit(FuncDef*)` variant string-replaces it in the body with the enclosing function's name wrapped in the dotcc `L("name\0"u8)` idiom. Fixture `predefined-ids/` (MSVC-oracle-validated for byte-identical line/file/function output). |
| Variadic macros `...` / `__VA_ARGS__` (C99) | ✅ | `OnDefine` detects a trailing `...` in the param list and sets `MacroDef.IsVariadic`. At expansion, extras beyond the named-param count are comma-joined and bound to the magic name `__VA_ARGS__` in the substitution map; references to that name in the body expand to the joined extras. Fixture `macro-ops/` (variadic `LOG(fmt, ...)` shape). |

## libc (`DotCC.Libc/`)

The runtime surface dotcc-emitted programs link against. Each function routes to a BCL primitive. The emitter still **inlines** copies of the helpers today; once `DotCC.Libc` is published to NuGet, emitted programs will reference the package.

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
| `atoi`, `atol`, `atof` | ❌ | `int.Parse` / `long.Parse` / `double.Parse` (invariant culture) |
| `strtol`, `strtod`, `strtoul`, `strtoll` (C99) | ❌ | Same with error-position out param |
| `abs`, `labs`, `llabs` (C99) | ❌ | `Math.Abs` |
| `rand`, `srand` | ❌ | Thread-local `Random` to match C's process-global seeding |
| `qsort`, `bsearch` | ❌ | Generic compare-callback; AOT-clean via function pointers |
| `getenv` | ❌ | `Environment.GetEnvironmentVariable` |
| `system` | ❌ | `Process.Start` (frontend exe is fine with this; emitted programs maybe) |
| `div`, `ldiv`, `lldiv` | ❌ | Quotient + remainder struct |

### `string.h`

| Function | Status | Notes |
|---|---|---|
| `strlen` | ✅ | Pointer loop |
| `strcmp` | ✅ | Pointer loop |
| `strncmp` | ❌ | Bounded `strcmp` |
| `strcpy` | ✅ | Pointer loop |
| `strncpy` | ❌ | Bounded `strcpy` |
| `strcat`, `strncat` | ❌ | Append to NUL-terminated dst |
| `strchr`, `strrchr` | ❌ | Find char in NUL-terminated string |
| `strstr` | ❌ | Find substring |
| `strtok` | ❌ | Stateful; thread-local cursor or use `strtok_r` (POSIX) |
| `strerror` | ❌ | Map errno to message |
| `memcmp` | ❌ | Trivial — `Span.SequenceCompareTo` |
| `memcpy` | ✅ | `Buffer.MemoryCopy` |
| `memmove` | ❌ | Same as memcpy but overlap-safe; `Buffer.MemoryCopy` is overlap-safe so add as an alias |
| `memset` | ✅ | `NativeMemory.Fill` |
| `memchr` | ❌ | Find byte in buffer |

### `math.h`

| Function | Status | Notes |
|---|---|---|
| `sin`, `cos`, `tan` | ❌ | `Math.Sin` etc. |
| `asin`, `acos`, `atan`, `atan2` | ❌ | `Math.Asin` etc. |
| `sinh`, `cosh`, `tanh` (+ inverses, C99) | ❌ | `Math.Sinh` etc. |
| `exp`, `log`, `log10`, `log2` (C99) | ❌ | `Math.Exp` etc. |
| `pow`, `sqrt`, `cbrt` (C99) | ❌ | `Math.Pow` / `Sqrt` / `Cbrt` |
| `ceil`, `floor`, `round`, `trunc` (C99) | ❌ | `Math.Ceiling` etc. |
| `fabs`, `fmod`, `fmin` (C99), `fmax` (C99) | ❌ | `Math.Abs` etc. |
| `NAN`, `INFINITY`, `isnan`, `isinf` (C99) | ❌ | `double.NaN`, `double.PositiveInfinity`, `double.IsNaN` |
| Type-generic `tgmath.h` (C99) | 🚫 | Needs C11 `_Generic` — not worth the cost |

### `ctype.h`

| Function | Status | Notes |
|---|---|---|
| `isalpha`, `isdigit`, `isalnum`, `isspace`, `isupper`, `islower`, `ispunct`, `isxdigit`, `iscntrl`, `isprint`, `isgraph` | ❌ | Trivial wrappers around `char.IsX((char)b)` with ASCII restriction |
| `toupper`, `tolower` | ❌ | `char.ToUpper`/`ToLower` ASCII only |

### `time.h`

| Function | Status | Notes |
|---|---|---|
| `time` | ❌ | `DateTimeOffset.UtcNow.ToUnixTimeSeconds` |
| `clock`, `CLOCKS_PER_SEC` | ❌ | `Environment.TickCount64` or `Stopwatch` |
| `difftime` | ❌ | Subtraction |
| `localtime`, `gmtime`, `mktime`, `strftime`, `asctime`, `ctime` | ❌ | DateTime mapping is non-trivial; lower priority |

### `assert.h`

| Function | Status | Notes |
|---|---|---|
| `assert(expr)` | ❌ | Macro that compiles to `Debug.Assert` |
| `static_assert` (C11) | ❌ | Compile-time check; could lower to `#error` if expr is constant-false |

### `errno.h`, `signal.h`, `setjmp.h`

| Header | Status | Notes |
|---|---|---|
| `errno.h` (errno, perror) | 🟡 | Thread-local int; map common values to BCL exceptions |
| `signal.h` | 🚫 | Out of scope — managed runtimes don't model POSIX signals usefully |
| `setjmp.h` (`setjmp`/`longjmp`) | 🚫 | Cannot be implemented safely on CLR — non-local jumps break C# stack invariants |

## Beyond C99

| Feature | C version | Status | Notes |
|---|---|---|---|
| Variadic macros, mixed decls/code, designated init, restrict, complex types, VLA, `_Bool`, `inline`, `__func__`, line comments | C99 | various above | dotcc's baseline target |
| `_Generic` | C11 | ❌ | Type-generic dispatch — complex; low priority |
| `_Static_assert` / `static_assert` | C11 | ❌ | Compile-time check |
| `_Noreturn` / `noreturn` | C11 | ❌ | Cosmetic — emit `[DoesNotReturn]` |
| Anonymous structs/unions | C11 | ❌ | Depends on `struct`/`union` |
| `_Thread_local` / `thread_local` | C11 | ❌ | Lower to `[ThreadStatic]` |
| `_Alignas`, `_Alignof` | C11 | ❌ | `[StructLayout(Pack=N)]` |
| Bounds-checked interfaces (Annex K) | C11 | 🚫 | Almost no real C uses these |
| `threads.h` | C11 | 🚫 | Use .NET threading directly when needed |
| `nullptr` constant | C23 | ❌ | Alias for `null` (or `(void*)0`) |
| `constexpr` | C23 | ❌ | Compile-time evaluation — useful for `enum`/`switch` |
| `typeof`, `typeof_unqual` | C23 | ❌ | Type inference — useful for macros |
| `[[attribute]]` syntax | C23 | ❌ | Map common ones (`[[noreturn]]`, `[[deprecated]]`) to `[DoesNotReturn]` etc. |
| `_BitInt(N)` | C23 | 🚫 | No C# equivalent; .NET 7+ `Int128`/`UInt128` only |
| `#embed` | C23 | ❌ | Embed file bytes — could lower to `byte[]` literal |
| `auto` (type inference) | C23 | 🚫 | Conflicts with C's old `auto` storage class; C# already has `var` if user wants this |

## Out of scope (won't implement)

Listed here so we don't relitigate them. All marked 🚫 above.

- **Trigraphs / digraphs** — removed in C23, never useful.
- **Wide string/char literals** (`L"…"`, `wchar_t`, `wprintf`) — dotcc is UTF-8-native; wide types add no value and complicate the lowering.
- **`long double`** — no C# equivalent.
- **`volatile`** — no useful semantic on managed runtimes; we just accept and ignore the qualifier.
- **`restrict`** — optimisation hint with no analogue in C#.
- **`setjmp`/`longjmp`** — non-local jumps can't be implemented safely on the CLR; programs that need them are out of dotcc's reach.
- **`signal.h`** — POSIX signal model doesn't map onto .NET.
- **`gets`** — removed in C11; security disaster.
- **`tgmath.h`** — depends on `_Generic`.
- **Annex K bounds-checked interfaces** — almost no real-world C uses them.
- **`threads.h` (C11)** — emitted code can call .NET threading directly.
- **`_BitInt(N)`** — only well-defined for two widths on .NET; not worth the complexity.

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
5. **Update this file**: flip the row from ❌ to ✅, mention the fixture in **Notes**.

The grammar conventions (precedence ladder, dangling-else handling, lexer ordering) live in `CLAUDE.md` under **Grammar conventions** — read that before touching `c.lalr.yaml`.
