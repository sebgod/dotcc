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
rather than miscompiling). `comptime` and generics are out of scope by design;
`std` is **not** modeled in general — only a curated set of allocator paths
(`std.mem.Allocator`, `std.heap.page_allocator`/`c_allocator`/`FixedBufferAllocator`;
see the Allocators section) resolves, everything else errors clearly. Legend: ✅
supported (parses **and** lowers + runs) · 🚧 parses but does not lower yet (loud
error at the use site) · 🚫 not supported.

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
| `[]T` / `[]const T` slice | ✅ | → the runtime fat pointer `Slice<T>` / `ConstSlice<T>` (`{ T* Ptr; ulong Len; }`, the C++ `std::span` shape — **not** C#'s ref-struct `Span<T>`, so a slice can be a struct field and cross the ABI; `AsSpan()` bridges to the BCL). `.len`/`.ptr`, `s[i]`, `s[lo..hi]`, array/string coercion, and `for` over it all work (rows below). **Deferred:** `[*]T`-backed slices, sentinel `[:0]T`, open-ended `s[lo..]`, by-ref `\|*x\|`, the non-escaping-stack → `stackalloc`+`Span` peephole |
| `[N]T` array (local) | ✅ | `var b: [N]T = undefined;` → a stackalloc'd C array (zero heap); `b[i]` indexes, `b[lo..hi]` yields a **stack-backed slice**. Size `N` must be an integer literal. **Init:** only `undefined` so far (positional `.{…}` / `[_]T{…}` array literals deferred) |
| tuple `struct { T1, T2, … }` | ✅ | an anonymous **positional** struct → C# `System.ValueTuple<…>` (Milestone G — see the **Tuples** section). Valid as a return / param / var type; a positional literal `.{a, b}` constructs it, `t[N]` (literal `N`) reads `.ItemN+1`, and `const a, const b = e` destructures. **Runtime subset only:** arity 1..7 (empty + >7 deferred); comptime / type-valued fields and a mixed positional+named literal are rejected |
| `std.mem.Allocator` | ✅ | the allocator fat pointer `{ ptr, vtable }` → the runtime `Allocator` value type (see the **Allocators** section). `std.heap.FixedBufferAllocator` is the concrete second allocator. Any OTHER `std.*` type errors clearly |
| `const P = struct { fields…, methods… };` | ✅ | container decl (top-level) → a real C# `unsafe struct` via the SHARED aggregate machinery the C frontend uses. Fields **and** methods (below) in the body; tagged unions are a later D slice. Empty `struct {}` allowed; `pub`-wrapped + in-function containers deferred |
| container **method** `fn m(self: P, …) …` | ✅ | a `fn`/`pub fn` in a struct, **enum, or union** body lowers to a free function `P_m` with the receiver as its first parameter. `p.m(a)` (UFCS) auto-refs/derefs the receiver to the declared `self` form (`&p` for a `*P` receiver, `p.*` for the reverse); `P.m(p, a)` and a no-receiver `P.assoc(a)` (associated function) call it directly. An enum receiver dispatches the same way; `self == .member` (enum equality with a result-located literal) is supported. **Deferred:** generic methods |
| namespaced value `const NAME = …;` | ✅ | a container-level `const` (a comptime value) read as `Type.NAME` in any of struct/enum/union. dotcc **inlines** the (lazily re-lowered) RHS at each use site — `const max: u8 = 42;` → `Cfg.max`, `const default = Color.blue;` → `Color.default`. **Deferred:** a container-level `var` (a mutable global — needs top-level globals; rejected loudly) and a const RHS that references a *sibling* const by bare name (qualify it as `Type.sibling`) |
| receiver type `self: @This()` | ✅ | `@This()` resolves to the enclosing container type (so `self: @This()` / `self: *@This()` name the receiver without repeating the name); explicit `self: P` / `self: *P` also work |
| self-type alias `const Self = @This();` | ✅ | a container-level `const` aliasing the container's own type inside its methods (the ubiquitous Zig idiom; any alias name). Resolves as a param/return/local type (`self: Self`, `… ) Self`), the base of a static call (`Self.init(…)`), and a typed literal (`Self{…}`) — all to the container, scoped per-container (two containers may each declare `const Self = @This();`). A **non-`@This()`** container `const` (a namespaced value constant) is rejected — it needs top-level globals, not yet lowered |
| `const C = enum(T) { … };` / `enum { … }` | ✅ | container decl → C# `enum C : T` (default underlying `int`); members auto-increment or take an explicit constant value. `@intFromEnum` decays to the underlying int. Methods (above) and a `const Self = @This();` alias are allowed in the body |
| `const U = union(enum) { a: T, b, … };` | ✅ | tagged union → the faithful C tagged-union shape: an outer struct `{ U_Tag __tag; U_Payload __payload; }` whose `__payload` is a NESTED `[StructLayout(Explicit)]` union overlaying every payload variant at offset 0 (the shared C-union machinery) — so payloads share storage, matching Zig's memory model. A void variant (`b`) is tag-only; an all-void union has no `__payload`. Construct with `.{ .a = v }` / `U{ .a = v }` (payload) or `.b` at a union sink (void). Direct `u.a` reads `u.__payload.a` (unchecked, like Zig release mode). Methods (above) and a `const Self = @This();` alias are allowed in the body. **Deferred:** untagged `union { … }`, explicit `union(SomeEnum)` |
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
| `switch (x) { v => {…}, a, b => {…}, else => {…} }` | ✅ | as a STATEMENT → the C IR Switch. Single / multi-value / `else` (→ default) prongs; NO fall-through (each prong gets an appended `break`). Switching on an **enum** works (subject + `.member` labels decay to the underlying int). Prong bodies must be braced **blocks**; bare-expr bodies, ranges (`a...b`), and switch-as-EXPRESSION are deferred |
| `switch (u) { .a => \|x\| {…}, … }` | ✅ | switch on a **tagged union** → dispatch on the `__tag`; a `\|x\|` payload capture binds the matched variant's payload (by value). An exhaustive union switch with no `else` makes its last prong the C# `default` (so the function provably returns). **Deferred:** by-reference `\|*x\|` capture, multi-variant capture prongs, capture on `if`/`while` (optionals / error-unions) |
| `return e;` / `return;` | ✅ | |
| `x = e;` assignment | ✅ | |
| `const a, const b = e;` destructure | ✅ | bind a tuple's elements to new locals (Milestone G) — desugars to a single-eval temp + per-element `.ItemN` reads (a brace-less sequence, so the binders stay in the enclosing scope). `const`/`var` binders, ≥2. **Deferred:** the assign-to-existing-lvalue form `a, b = e;` (a grammar-level cut, so a parse error) and typed binders (`const a: T, …`) |
| `_ = e;` discard | ✅ | Zig's mandatory discard of a non-void result |
| block `{ … }` | ✅ | |
| `defer Stmt;` | ✅ | scope-exit cleanup — runs on EVERY exit from the enclosing block (fall-through, `return`, `break`, `continue`, a propagating error), in LIFO declaration order. → C# `try { rest } finally { cleanup }`. The deferred `Stmt` is an `expr;`, a `_ = expr;` discard, or a braced block. See the **Defer** section |
| `errdefer Stmt;` | ✅ | error-exit cleanup — runs only when the block exits via a propagating error, LIFO-interleaved with `defer`. → C# `try { rest } catch (ZigErrorReturn) { cleanup; throw; }`. A function with an `errdefer` routes its `return error.X` through a throw so it reaches the catch. **Deferred:** `errdefer \|e\| …` payload capture |
| `for (a..b) \|i\| …` (range for) | ✅ | → C `for (usize i = a; i < b; i++)`; the `\|i\|` capture is the usize loop index (`\|_\|` discards). The body is a Stmt or block |
| `for (s) \|x\|` / `for (s, 0..) \|x, i\|` (for-over-slice) | ✅ | iterate a slice's elements (`x` = a per-iteration copy); the index form also binds the usize index. → C `for (usize __i=0; __i<s.Len; __i++) { var x = s.Ptr[__i]; [var i = __i + 0;] … }` (the slice is hoisted to a temp unless a bare var) |
| open-ended `s[lo..]`, by-ref `\|*x\|`, labeled loops, labeled `break`/`continue`, switch ranges/expr | 🚫 | |

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
| postfix `p.*` (deref), `a[i]` (index) | ✅ | pointer deref / subscript → the C `Unary(Deref)` / `Index` IR. On a slice, `s[i]` indexes through `.Ptr` (`s.Ptr[i]`) |
| `t[N]` (tuple index) | ✅ | a **literal** `N` into a tuple → `.ItemN+1` (Milestone G); a runtime index is rejected (a tuple field is statically named, not addressed) |
| `s[lo..hi]` (slicing) | ✅ | → a sub-slice fat pointer `{ s.ptr + lo, (ulong)(hi - lo) }` (base may be a slice, pointer, or array) |
| `s.len` / `s.ptr` (slice fields) | ✅ | the fat pointer's length (`ulong`) and data pointer (`T*`) |
| `@as(T, expr)` | ✅ | explicit-type cast → the C `Cast` IR (`@as(c_int, 42)` for a variadic arg) |
| `null` literal | ✅ | optional none / null pointer (renders C# `null`) |
| `undefined` | ✅ | uninitialized storage. An array local takes the stackalloc path; a scalar → `default(T)` (a zeroed over-approximation — a correct program writes before reading) |
| postfix `.?` (optional unwrap) | ✅ | value optional → `.Value` (panics on none); optional pointer → identity (V1: no null-check) |
| `a orelse b` (value RHS) | ✅ | value optional → C# `??` (single-eval, lazy `b`); pointer → `a != null ? a : b` (simple LHS; `orelse return` is Milestone B2) |
| prefix `try` | ✅ | unwrap an error union's payload, or PROPAGATE its error out of the enclosing `!T` fn (exception-based early-return, modeled on the setjmp lowering). `try e;` as a statement works too |
| `e catch fallback` | ✅ | the payload on success, else the fallback. The fallback must be side-effect-free (literal / variable) — a non-trivial one is rejected (deferred), as is `catch \|e\| …` capture and `catch return` |
| `error.Foo` | ✅ | an error value — only in `return error.Foo;` within a `!T` fn (a bare error value / error-set decls deferred) |
| postfix `.field` | ✅ | struct field access → the shared `Member` IR (field type from the aggregate table). Zig has no `->`, so `p.field` on a `*T` auto-derefs (emits C# `->`). `EnumName.member` resolves here too → an `EnumConstRef` |
| `.{ .f = v, … }` (anonymous struct literal) | ✅ | result-located → `new T { f = v }` from the sink type (a typed decl, return, assignment, call arg, or field). Empty `.{}` zero-inits |
| `T{ .f = v, … }` (typed struct literal) | ✅ | Zig's `CurlySuffixExpr <- TypeExpr InitList?` — the type is named, so NO sink is needed; valid in any position, incl. sink-less ones like `(T{…}).field`. A dedicated `CurlySuffix` grammar level (above `Type`) makes it conflict-free against `fn f() RetType {` (the return type stays a raw `Type`, no init list) — no rewriter. `&T{…}` (address of a temporary) materializes a block-local temp and takes its address (the same shared-backend path as C's `&(T){…}`) |
| `.{ a, b, … }` (positional tuple literal) | ✅ | result-located → `new System.ValueTuple<…>(a, b)` (Milestone G); element types come from a tuple sink, or are inferred from the elements (`const t = .{a, b};`). Shares the `.{…}` surface with the named struct literal — a literal that MIXES positional + named is rejected |
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

`comptime` (beyond const folding), generics / `anytype`, `@import("std")` beyond the curated
allocator paths (`std.mem.Allocator` + `std.heap.page_allocator`/`c_allocator`/
`FixedBufferAllocator` ARE supported — see the Allocators section), untagged
`union { … }` + explicit `union(SomeEnum)` + `opaque` (data-only `struct`/`enum`/`union`
**with methods** — struct/enum/union methods + the `const Self = @This();` self-type alias
+ namespaced VALUE `const`s ARE supported — see below), container-level `var` (a namespaced
mutable global — needs top-level global storage),
explicit error-SET declarations (`error{A,B}` —
inferred `!T` + `error.X` ARE supported), `async`/`suspend`, inline assembly. (Both
`.{…}` and typed `T{…}` init lists ARE supported, including `&T{…}` —
address-of-a-temporary — via a materialized block-local temp.)

### Why these are out of scope — the reasoning, not just the line

The cuts aren't arbitrary. dotcc is a **syntax-directed transpiler**: it lowers parsed
syntax to a C-shaped IR and emits C# (or wat). It has **no compile-time evaluation
engine**, and it targets a **managed VM**, not native machine code. Nearly every item
above falls out of one of those two facts.

**The comptime root** — catches `comptime`, generics / `anytype`, and a real `std`. These
are one wall, not three. `comptime` is partial evaluation (arbitrary Zig run by the
compiler at build time — loops, branches, type construction); supporting it properly means
*building a Zig interpreter*, which dotcc is not (it only constant-folds, like its C
front-end). Zig generics are comptime-driven **monomorphization** — a fresh,
differently-*shaped* function body instantiated per type at each call site — so they can't
map onto C# generics (which can't change a body's shape per `T`, and have no notion of a
*value*- or *type*-valued generic argument); doing it yourself needs the interpreter.
Generics ⊂ comptime. And `std` is generics- and comptime-soaked top to bottom, so a
faithful `std` needs both — hence the curated-paths resolver (model only what maps cleanly
to the runtime: the allocator, libc; error loudly on the rest) instead of a real `std`
model. The biggest tuple/`.{…}` consumer — `std.fmt`'s `print("{} {}", .{a, b})` — lives
here too (comptime reflection over the arg tuple), and is already side-stepped by routing
formatting through `extern fn printf` + libc.

**The managed-target root** — catches inline assembly and `async`/`suspend`. Inline `asm`
emits raw target machine code; the C# and wat backends run on a VM with nowhere to put it
(C# has no inline-asm escape hatch) — untranslatable by construction, the same wall the C
front-end hits. `async`/`suspend` is a double miss: Zig's stackless coroutines with an
explicit, caller-owned `@Frame` (take `&frame`, store it, `resume` it by hand) don't map
onto .NET's scheduler-driven `async`/`await` without a lossy translation — *and* async was
removed from the pinned Zig, so it's a feature the reference compiler doesn't even have
(the differential oracle couldn't validate it anyway).

**The soft case** — destructuring assignment was "not yet", not "can't", and it has now
landed (Milestone G — see the **Tuples** section). It needed tuple types (positional anonymous
structs), which lower cleanly onto C# `ValueTuple` for the **runtime** subset — value semantics,
positional access, comptime-known fixed arity, and native deconstruction. Only the comptime
*flavor* of tuples stays out (type-valued / `comptime_int` fields, and the `std.fmt` reflection
idiom above) — the comptime root again.

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

## Allocators — devirtualize the default, vtable for the rest

dotcc models Zig's `std.mem.Allocator` as a fat pointer `{ ptr, vtable }` (the runtime
`Allocator` value type in `DotCC.Libc/ZigAlloc.cs`, auto-spliced) whose high-level
`a.alloc(T, n)` / `a.free(s)` dispatch through a vtable of raw function pointers — `alloc`
returns `Error![]T` (an `ErrUnion<Slice<T>>`, composing with `try`/`catch` above). Two
allocators ship: the C heap (the `std.heap.page_allocator`/`c_allocator` default, backed by
`Libc.malloc`/`free`) and `std.heap.FixedBufferAllocator` (a deterministic bump allocator
over a caller buffer — the second allocator that exercises real indirect dispatch).

**Devirtualization is the optimization layer.** At each `a.alloc(…)` site the lowering asks
*can it prove which concrete allocator `a` is?*

| Form | Lowers to |
|---|---|
| `const a = std.heap.page_allocator;` / `c_allocator` | comptime binding (no decl); `@import("std")` likewise |
| `a.alloc(T, n)` / `a.free(s)` on the **provable** default | **DIRECT** `ZigAlloc.AllocCHeap<T>` / `FreeCHeap<T>` (a `Libc.malloc`/`free`, no vtable) |
| `var fba = std.heap.FixedBufferAllocator.init(&buf);` | `FixedBufferAllocator.Init((byte*)buf, N)` |
| `fba.allocator()` | `ZigAlloc.FbaAllocator(&fba)` → a runtime `Allocator` (opaque) |
| `a.alloc(T, n)` on an **opaque** `a` (a `std.mem.Allocator` param, or `fba.allocator()`'s result) | **INDIRECT** `a.Alloc<T>(n, oom)` — the genuine `vtable->alloc(ctx, bytes)` dispatch, inside the runtime |
| the default passed to an opaque `std.mem.Allocator` sink | materialized `ZigAlloc.CHeap()` (a runtime fat pointer; its vtable still reaches the C heap) |

So the default stays a direct call; only a genuinely runtime-selected allocator pays the
indirect dispatch. Example: `examples/zig-alloc/main.zig`.

**V1 limits** (documented, not silent): only the C-heap default is *provable* — an
`fba.allocator()` result and every `std.mem.Allocator` parameter are opaque (indirect), even
where a local could in principle be proven; `a.create(T)` / `a.destroy(p)` (single-object
alloc → `Error!*T`, an error-union-over-pointer) and `resize`/`remap`/`realloc` are deferred
with a clear error; and `std` is a known-paths resolver, not a real std model — anything outside
the allocator paths above errors clearly. (`defer a.free(buf)` — the idiomatic, every-path release
— now works; see the **Defer** section.)

## Tuples — runtime tuples → C# `ValueTuple`

A Zig tuple is an anonymous **positional** struct — `.{ a, b }`, type `struct { T1, T2 }`, accessed
`t[0]`/`t[1]`. dotcc lowers the **runtime** subset directly onto C# `System.ValueTuple<…>`
(Milestone G): the same value semantics, positional access, comptime-known fixed arity, and native
deconstruction. The headline use is multiple return values.

| Form | Lowers to |
|---|---|
| tuple TYPE `struct { T1, T2, … }` (return / param / var) | `System.ValueTuple<T1, …>` (arity-uniform, incl. arity 1) |
| positional literal `.{ a, b }` | `new System.ValueTuple<…>(a, b)` — element types from the tuple sink, or inferred from the elements |
| `t[N]` (literal `N`) | `.ItemN+1` (ValueTuple's 1-based fields) |
| `const a, const b = e;` | a single-eval temp + per-binder `.ItemN` reads (a brace-less `Seq`, so the binders land in the enclosing scope) |

So a function returns `struct { u8, u8 }`, the caller writes `const lo, const hi = minmax(…);`, and
both sides are plain `ValueTuple` — no custom runtime. Example: `examples/zig-tuple/main.zig`.

**Why `ValueTuple` and not a `Span`-style type:** a `ValueTuple` is a value type (copied on
assignment), positional, fixed-arity, deconstructs natively, and is `unmanaged` when its elements
are (so it can be a struct field / cross the ABI) — the same property that justified `Slice<T>`. The
fit is exact for the runtime subset; only the comptime *flavor* of tuples (type-valued /
`comptime_int` fields, and the `std.fmt` `.{…}` reflection idiom — already handled via
`extern fn printf`) stays out, the comptime root again.

**V1 limits** (documented, not silent): arity 1..7 (an empty tuple and arity > 7 — which would need
ValueTuple's `TRest` nesting — are deferred); the assign-to-existing-lvalue destructure `a, b = e;`
is a grammar-level cut (a parse error — V1 binders are `const`/`var` only); a literal that mixes
positional + named fields is rejected; and a runtime (non-literal) tuple index is rejected.

## Defer / errdefer — scope-exit cleanup → C# try/finally + try/catch

`defer Stmt;` registers a cleanup that runs when control leaves the enclosing block — on EVERY
exit (fall-through, `return`, `break`, `continue`, or a propagating error), in LIFO declaration
order. `errdefer Stmt;` is the same but fires only when the block exits via a **propagating
error**. The two share one LIFO cleanup stack (a later-declared `errdefer` runs before an
earlier `defer`). The headline use is pairing an allocation with its release:
`const buf = try a.alloc(u8, n); defer a.free(buf);`.

dotcc lowers a block's defers by **restructuring**: each `defer`/`errdefer` wraps the statements
that follow it within its block, nested in lexical order — so the nesting itself yields the LIFO
order, the same shape as the C front-end's `setjmp` try-guard.

| Form | Lowers to |
|---|---|
| `defer cleanup;` | `try { rest-of-block } finally { cleanup }` (C#'s finally fires on every exit) |
| `errdefer cleanup;` | `try { rest-of-block } catch (ZigErrorReturn) { cleanup; throw; }` (the rethrow keeps the error propagating to the `!T` boundary) |
| `return error.X;` in a fn that has an `errdefer` | `throw new ZigErrorReturn(code);` (NOT a direct `Err` return — see below) |

**The `errdefer` ⇄ `return error.X` seam.** An `errdefer` is a C# `catch`, which only fires on a
THROWN error. But `return error.X;` normally lowers to a *direct* `ErrUnion<T>.Err(code)` return
(Milestone B2), which a catch can't observe. So when the enclosing function contains an `errdefer`,
its error returns are instead routed through a thrown `ZigErrorReturn` — propagating through the
errdefer catch(es) on the stack, with the existing `!T` boundary catch still converting it back to
an `Err`. This unifies both error-exit paths (`try`-propagation and explicit `return error.X`) to one
mechanism. A function with **no** `errdefer` keeps B2's elegant, exception-free direct `Err` return
untouched. (`defer` needs no such rewrite — a C# `finally` fires on a direct return too.) Example:
`examples/zig-defer/main.zig`.

**V1 limits** (documented, not silent): `errdefer |e| …` payload capture is deferred (the grammar's
`errdefer Stmt` has no `|e|`); a control-flow statement inside a defer (`defer return;` /
`break` / `continue` — which Zig itself rejects) would emit an illegal C# `finally { return; }`
(CS0157) rather than a faithful loud reject (a later polish).

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
  `examples/zig-struct-typed` (typed `T{…}` literal in value + sink-less positions),
  `examples/zig-methods` (struct methods + UFCS: static `init`, pointer-receiver `scale`, `@This()` value receiver),
  `examples/zig-union` (tagged `union(enum)`: payload + void variants, `switch` with `\|x\|` capture),
  `examples/zig-slices` (`[]const u8` slices: `.len`/`.ptr`, index, `s[lo..hi]`, `for (s) \|b\|`),
  `examples/zig-alloc` (allocators: devirt'd `page_allocator`, a `FixedBufferAllocator` via the
  indirect vtable, an opaque `std.mem.Allocator` param + materialized default),
  `examples/zig-tuple` (tuples: a `struct { u8, u8 }` multiple-return + `const lo, const hi = …`
  destructure, a tuple-typed parameter, an inline-literal destructure, a literal `t[N]` index),
  `examples/zig-defer` (defer/errdefer: `defer a.free(buf)` pairing a FixedBufferAllocator
  allocation with its release, plus an `errdefer` step that fires on the error path).
