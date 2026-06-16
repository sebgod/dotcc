# ZIG-SUPPORT.md

What dotcc's **Zig front-end** supports today. The Zig axis is the second
implementer of the `IFrontend` seam (the C front-end is the first): a `.zig`
input is lexed + parsed by the LALR(1) grammar in [`DotCC.Lib/zig.lalr.yaml`](DotCC.Lib/zig.lalr.yaml)
(→ generated `DotCC.Zig`), lowered to the neutral typed IR by
[`ZigLowering`](DotCC.Lib/Frontends/ZigLowering.cs), and emitted by the **same**
C# backend + shell as C. So the lowered surface inherits dotcc's whole runtime
for free — including the libc routing that makes `printf` print.

This is an honest **subset dialect**, not full Zig: the grammar parses a
C-shaped value/type core, and lowering grows behind it deliberately ("fail
loudly, grow on purpose" — anything unlowered throws `IrUnsupportedException`
rather than miscompiling). `comptime`, generics, and `std` are out of scope by
design. Legend: ✅ supported (parses **and** lowers + runs) · 🚧 parses but does
not lower yet (loud error at the use site) · 🚫 not supported.

## Design intent — C interop without `@cImport`

Modern Zig **removed `@cImport`** (deprecated 0.16, gone in the 0.17-dev pin);
standalone-file C translation no longer exists (it's a build-system
`translate-c` step now). So dotcc's Zig C/libc interop uses the modern,
standalone-valid path: an **`extern fn` prototype + link libc (`-lc`)**. dotcc
marks such a prototype `FromSystemHeader` and renders the call **by its bare
name**, which routes it to dotcc's own `Libc` runtime — exactly how a C
program's libc call is handled. No `@cImport`, no header harvest.

## Declarations

| Feature | Status | Notes |
|---|---|---|
| `fn name(params) Ret { … }` | ✅ | top-level function definition |
| `pub fn main() void` | ✅ | void-returning main — shell calls it for effect, returns 0 |
| `pub fn main() u8` | ✅ | the `u8` return is the process exit code |
| `pub fn …` | ✅ | `pub` unwrapped (visibility is a no-op in our single-module emit) |
| Parameters `name: Type` | ✅ | names + types ride into the C# signature; faithful signedness |
| Forward references | ✅ | two-pass lowering (Zig has no prototypes) — a call may precede the callee |
| `extern fn f(p: T) Ret;` | ✅ | libc/FFI prototype (no body); routed by bare name, linked with `-lc` |
| `extern fn f(p: T, ...) Ret;` | ✅ | **variadic** extern (e.g. `printf`); `...` must be last + extern-only. A variadic argument must have a fixed-size type — `printf("%d", @as(c_int, 42))`, never a bare `printf("%d", 42)` (rejected, matching real zig — see below) |
| local `const`/`var` (typed or inferred) | ✅ | inside a function body |
| `fn f() !T` (inferred-error return) | ✅ | the `!T` returns an error union (`ErrUnion<T>`); see `try`/`catch`/`error.X` below. V1 erases the error SET |
| `pub fn main() !void` / `!u8` (error-union main) | 🚫 | main itself returning an error union is deferred — error unions live in helper fns; main stays `void`/`u8` |
| top-level / global `const`/`var` | 🚫 | only function-local decls lower today |
| `export`/`inline`/`callconv`/`align`/`linksection` | 🚫 | full FnProto modifiers not modeled |
| `extern "c"` library-name string | 🚫 | bare `extern fn` only |

## Types

| Feature | Status | Notes |
|---|---|---|
| `i8 i16 i32 i64`, `u8 u16 u32 u64` | ✅ | faithful signedness (i8→`sbyte`, u8→`byte`, …) |
| `usize`/`isize` | ✅ | LP64 pointer-width (`ulong`/`long`) |
| `f32`/`f64` | ✅ | → C# `float`/`double` |
| `bool`, `void` | ✅ | |
| `c_char c_short c_ushort c_int c_uint c_long c_ulong c_longlong c_ulonglong` | ✅ | C-ABI types for `extern fn`, LP64-shaped |
| `*T`, `*const T` | ✅ | pointer (pointee `const` rides as a type qualifier) |
| `[*c]T`, `[*c]const T` | ✅ | C pointer (== C's `T*` / `const T*`) — printf's `[*c]const u8` format |
| `?T` optional | ✅ | `?*T` → bare nullable `T*` (niche); `?T` over a value → C# `Nullable<T>`. `null`/`.?`/`orelse` below |
| `[]T` slice | 🚧 | parses; the fat-struct lowering is not built |
| `[N]T` array | 🚧 | parses; does not lower |
| `const P = struct { … };` | ✅ | container decl (top-level) → a real C# `unsafe struct` via the SHARED aggregate machinery the C frontend uses. Data-only fields; methods (UFCS) + tagged unions are later D slices. Empty `struct {}` allowed; `pub`-wrapped + in-function containers deferred |
| `const C = enum(T) { … };` / `enum { … }` | ✅ | container decl → C# `enum C : T` (default underlying `int`); members auto-increment or take an explicit constant value. `@intFromEnum` decays to the underlying int |
| `E!T` / `!T` error-union type | ✅ | → runtime `ErrUnion<Payload>` (`ErrUnion<Unit>` for `!void`). V1 erases the error SET `E` — `anyerror!T` / named `E!T` lower identically (payload only) |
| `comptime_int`/`comptime_float`, arbitrary `iN`/`uN` | 🚫 | |
| `[*]T` many-item, `[*:s]T` sentinel | 🚫 | only `[*c]` is tokenized |

## Statements

| Feature | Status | Notes |
|---|---|---|
| `if (c) … else …` | ✅ | condition wrapped in `Cond.B(…)` for C-truthy semantics |
| `while (c) …` | ✅ | (no payload capture yet) |
| `while (c) : (cont) …` | ✅ | the continue-expression → the C IR `for`-post, so `continue` runs `cont` (faithful to Zig). The common assignment cont `: (i = i + 1)` and a bare-expr cont both parse |
| `break;` / `continue;` | ✅ | unlabeled — reuse the C IR loop-control nodes (labeled `break :blk` deferred) |
| `switch (x) { v => {…}, a, b => {…}, else => {…} }` | ✅ | as a STATEMENT → the C IR Switch. Single / multi-value / `else` (→ default) prongs; NO fall-through (each prong gets an appended `break`). Switching on an **enum** works (subject + `.member` labels decay to the underlying int). Prong bodies must be braced **blocks**; bare-expr bodies, ranges (`a...b`), payload capture `\|x\|`, and switch-as-EXPRESSION are deferred |
| `return e;` / `return;` | ✅ | |
| `x = e;` assignment | ✅ | |
| `_ = e;` discard | ✅ | Zig's mandatory discard of a non-void result |
| block `{ … }` | ✅ | |
| `for (a..b) \|i\| …` (range for) | ✅ | → C `for (usize i = a; i < b; i++)`; the `\|i\|` capture is the usize loop index (`\|_\|` discards). The body is a Stmt or block |
| `for (slice) \|x\|`, `0..` open-ended, `for (s, 0..) \|x, i\|`, `defer`/`errdefer`, labeled loops, labeled `break`/`continue`, switch ranges/expr | 🚫 | for-over-slice needs slices (later milestone) |

## Expressions

| Feature | Status | Notes |
|---|---|---|
| integer / float / string literals | ✅ | decimal int; string reuses C escape decoding (`\n \t \\ \" \xNN`) |
| identifiers, `(grouped)` | ✅ | |
| `or` `and` (short-circuit) | ✅ | |
| comparison `== != < > <= >=` | ✅ | non-associative (`a < b < c` is a parse error, like Zig) |
| bitwise `& ^ \|`, shift `<< >>` | ✅ | |
| arithmetic `+ - * / %` | ✅ | usual-arithmetic result typing (fixes i64 truncation) |
| prefix `-` `~` `!` | ✅ | |
| `if (c) a else b` (if-**expression**) | ✅ | → C# ternary |
| function call `f(args)` | ✅ | intra-Zig + forward-ref + libc-by-bare-name (incl. variadic `printf`) |
| prefix `&` (address-of) | ✅ | `&x` → `*T`; a var/param operand is marked address-taken |
| postfix `p.*` (deref), `a[i]` (index) | ✅ | pointer deref / subscript → the C `Unary(Deref)` / `Index` IR |
| `@as(T, expr)` | ✅ | explicit-type cast → the C `Cast` IR (`@as(c_int, 42)` for a variadic arg) |
| `null` literal | ✅ | optional none / null pointer (renders C# `null`) |
| postfix `.?` (optional unwrap) | ✅ | value optional → `.Value` (panics on none); optional pointer → identity (V1: no null-check) |
| `a orelse b` (value RHS) | ✅ | value optional → C# `??` (single-eval, lazy `b`); pointer → `a != null ? a : b` (simple LHS; `orelse return` is Milestone B2) |
| prefix `try` | ✅ | unwrap an error union's payload, or PROPAGATE its error out of the enclosing `!T` fn (exception-based early-return, modeled on the setjmp lowering). `try e;` as a statement works too |
| `e catch fallback` | ✅ | the payload on success, else the fallback. The fallback must be side-effect-free (literal / variable) — a non-trivial one is rejected (deferred), as is `catch \|e\| …` capture and `catch return` |
| `error.Foo` | ✅ | an error value — only in `return error.Foo;` within a `!T` fn (a bare error value / error-set decls deferred) |
| postfix `.field` | ✅ | struct field access → the shared `Member` IR (field type from the aggregate table). Zig has no `->`, so `p.field` on a `*T` auto-derefs (emits C# `->`). `EnumName.member` resolves here too → an `EnumConstRef` |
| `.{ .f = v, … }` (anonymous struct literal) | ✅ | result-located → `new T { f = v }` from the sink type (a typed decl, return, assignment, call arg, or field). Empty `.{}` zero-inits |
| `T{ .f = v, … }` (typed struct literal) | ✅ | Zig's `CurlySuffixExpr <- TypeExpr InitList?` — the type is named, so NO sink is needed; valid in any position, incl. sink-less ones like `(T{…}).field`. A dedicated `CurlySuffix` grammar level (above `Type`) makes it conflict-free against `fn f() RetType {` (the return type stays a raw `Type`, no init list) — no rewriter. `&T{…}` (address of a temporary) materializes a block-local temp and takes its address (the same shared-backend path as C's `&(T){…}`) |
| `.enumLiteral` | ✅ | a bare `.member` resolves against its sink (typed decl / return / assignment / call arg / switch subject) → an `EnumConstRef` (`EnumName.member`). Untyped (no sink) is rejected, as Zig requires |
| `@intFromEnum(e)` | ✅ | the enum's integer value → decay to the underlying type (the C enum→int decay) |
| other `@builtin(...)` (`@intCast`/`@ptrCast`/…) | 🚧 | parse only — Zig 0.16's forms are result-location-typed (single arg), needing context-type inference dotcc lacks |
| wrapping/saturating ops | 🚫 | |

## Lexer

| Feature | Status |
|---|---|
| decimal integers, floats, `"…"` strings, `//` line comments, `@name` builtins | ✅ |
| hex/oct/bin/underscored integers, char literals `'x'`, multiline `\\` strings, `\u{…}` escapes, escaped-quote `\"` in a string | 🚫 |

## Out of scope (the dialect line)

`comptime` (beyond const folding), generics / `anytype`, `@import("std")`, struct
**methods** (UFCS) + tagged `union(enum)` + `opaque` (later D slices; data-only
`struct`/`enum` ARE supported), explicit error-SET declarations (`error{A,B}` —
inferred `!T` + `error.X` ARE supported), `async`/`suspend`, inline assembly,
destructuring assignment. (Both `.{…}` and typed `T{…}` init lists ARE supported,
including `&T{…}` — address-of-a-temporary — via a materialized block-local temp.)

## Mixed `.c` + `.zig` translation units

A single invocation may mix C and Zig: `dotcc main.c helper.zig -o app`. Both
groups lower into **one** IR module (the C group builds the module — preprocessor,
structs, globals — and the Zig group lowers into it), so the program emits once and
a call across the language boundary resolves at the C# level (every function is a
`DotCcProgram` method called by bare name). Each side declares the other's functions
the normal way — a C prototype (`int add(int, int);`) for a Zig function, an
`extern fn` for a C function — and they link. C structs/enums are preserved.

```c
// helper.c
int add(int a, int b);            // defined in add.zig
int main(void){ return add(40, 2); }
```
```zig
// add.zig
pub fn add(a: c_int, b: c_int) c_int { return a + b; }
```

Limits: `-shared` / `-l` import mode combined with a mixed set is not validated yet
(single-language only); cross-language **struct/type sharing** is moot until the Zig
front-end emits aggregates.

## Error unions — `try` is exception-based (the setjmp pattern, reused)

A `!T` function lowers to the runtime value type `ErrUnion<T>` (`DotCC.Libc/ZigErr.cs`,
auto-spliced like `CBool`) — either a payload (`Code == 0`) or a non-zero error code.
`return e;` → `ErrUnion<T>.Ok(e)`, `return error.Foo;` → `ErrUnion<T>.Err(code)`, and a
`!void` body that falls off the end returns `ErrUnion<Unit>.Ok(default)` (C# has no
generic-over-void, so `!void` uses the `Unit` payload).

The hard part is `try e`: it must unwrap the payload OR abort the *current* function with
the error, from **any** expression position (`const v = try f() + 1;`). Structured C# can't
express early-return-out-of-an-expression — so dotcc reuses the exact mechanism the C
front-end uses for `setjmp`/`longjmp`: `try e` lowers to `ErrUnion.Try(e)`, which throws a
private `ZigErrorReturn` on error, and every `!T` function body is wrapped in
`try { … } catch (ZigErrorReturn __e) { return ErrUnion<T>.Err(__e.Code); }`, converting the
propagated error back into an error-union return. `e catch fallback` doesn't propagate, so it
lowers to the plain `ErrUnion.Catch(e, fallback)` instead.

**V1 limits** (documented, not silent): the error SET is erased to one flat global code
space (`!T` / `anyerror!T` / `E!T` lower identically); the payload must be a value type (an
error union over a *pointer* is deferred — a C# generic can't take a pointer arg);
`catch`'s fallback must be side-effect-free; and `catch |e| …` capture, `catch return`,
explicit `error{…}` set decls, and an error-union `main` are all deferred.

## Strictness — dotcc tracks Zig where it matters

dotcc lowers Zig onto the same C-shaped IR + C# backend as C, which is C-lenient by
default (it silently inserts narrowing casts, applies C's default argument promotions,
etc.). On top of that core the Zig front-end layers the few **Zig-specific strictness
rules** the differential oracle proves we need — dotcc should reject what real `zig`
rejects, not silently accept more.

- **Variadic literals must be cast.** A bare integer/float literal is a `comptime_int` /
  `comptime_float` (no fixed-size ABI type), so Zig forbids passing it to a C-variadic.
  `printf("%d", 42)` is an error in both zig and dotcc; pass `@as(c_int, 42)`, a typed
  value, or any expression with a concrete-typed leaf (`x`, `f()`, `a + x`). dotcc emits
  zig's exact message through the same diagnostics channel C uses for constraint
  violations. (This one was found by the CI oracle catching dotcc being too lenient.)

## Validation

- **Always-on emit tests** — `DotCC.Tests/ZigFrontendTests.cs` pins dotcc's
  lowered C# for the supported surface (parameters, signedness, if/while, the
  if-expression, calls + forward refs, `extern fn` libc calls, variadic
  `printf`).
- **Differential oracle (opt-in)** — `DotCC.FunctionalTests/ZigOracleTests.cs`
  compiles + runs each program through dotcc **and** the real `zig` compiler and
  asserts they agree on exit code **and** stdout (`DOTCC_RUN_ZIG_ORACLE=1`; skips
  when no `zig` is on PATH). CI runs it on linux-x64 + windows-x64 against a
  pinned `zig 0.16.0`.
- **Examples** — `examples/zig-hello`, `examples/zig-extern` (putchar),
  `examples/zig-printf` (variadic printf), `examples/zig-optional` (`?T` / `null` /
  `.?` / `orelse`), `examples/zig-errunion` (`!T` / `try` / `catch` / `error.X`),
  `examples/zig-controlflow` (while-cont / `break` / `continue` / `switch` / range-`for`),
  `examples/zig-struct` (`struct` + `.{…}` + field access, `enum` + `.member` + `@intFromEnum` + enum `switch`),
  `examples/zig-struct-typed` (typed `T{…}` literal in value + sink-less positions).
