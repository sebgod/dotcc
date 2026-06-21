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
| `pub fn main() !void` / `!u8` (error-union main) | ✅ | main may return an error union (Milestone N, part 4) — emitted as `ErrUnion<…>`; the process entry maps the result like real zig: an error → exit 1 (the flat error code reported to stderr, stdout stays clean), success → exit 0 (a `!void` payload) or the integer payload value (`!u8`). `try` inside main propagates to that boundary |
| top-level / global `const`/`var` | ✅ | a runtime global → a `public static` field of `DotCcGlobals` (the same path the C front-end's file-scope variables take), surfaced by bare name (a function body reads/writes it unqualified). Typed keeps its annotation; untyped infers from the initializer (`const N = 5;` → `int`). Initializers are lowered in source order, so a global may reference an EARLIER global by bare name. `const`-ness isn't enforced (both lower to a mutable field — observably identical for a correct Zig program). An aggregate (struct), `[N]T` array, and `undefined` global are supported (Milestone K — an array routes through a pinned, program-lifetime backing store). **Deferred:** a fn-pointer global and a forward reference to a LATER global |
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
| `[N]T` array (local) | ✅ | `var b: [N]T = …;` → a stackalloc'd C array (zero heap); `b[i]` indexes, `b[lo..hi]` yields a **stack-backed slice**. Size `N` must be an integer literal. **Init:** `undefined` (zeroed) OR an array literal (Milestone K) — `.{…}` at a `[N]T` sink, typed `[N]T{…}` (explicit length), or `[_]T{…}` (length inferred from the element count). An empty literal is rejected (use `undefined`); returning an array literal by value is out of scope (arrays lower to pointers) |
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
| `if (opt) \|x\| {…} else {…}` (optional capture) | ✅ | bind a value/pointer optional's payload in the then-branch (Milestone M, part 1). A value optional `?T` → hoist the condition to a single-eval temp, `if (Cond.B(__cap.HasValue)) { var x = __cap.Value; … } else { … }`; a niche optional pointer `?*T` (a bare `T*`) → a non-null test with `x` bound to the unwrapped pointer (the same value). `\|_\|` tests without binding; the `else` is optional. **Deferred:** the by-ref `\|*x\|` form (part 4) |
| `if (eu) \|x\| {…} else \|e\| {…}` (error-union capture) | ✅ | bind an error union's success payload to `x` in the then-branch, the error to `e` in the else-branch (Milestone M, part 3). → a value inspection of the runtime `ErrUnion<T>`: `if (Cond.B(__cap.IsErr)) { var e = __cap.Code; … } else { var x = __cap.Value; … }` — NOT a propagating `try`, so the error is handled here and never reaches the function-boundary catch. `else \|_\|` discards the error. **V1:** `e` is the erased `ushort` error code; OPERATING on it (`e == error.X`, `@errorName`, propagation) awaits the error-set milestone. dotcc also leniently accepts a plain `else` on an error union (real zig requires `\|e\|`/`\|_\|`) |
| `while (c) …` | ✅ | (scalar/optional-less condition) |
| `while (opt) \|x\| …` (capture-while) | ✅ | optional payload capture-while (Milestone M, part 2). The condition is re-evaluated each iteration (it commonly advances an iterator); while it's non-null, `x` is bound and the body runs, else the loop exits. → `while (true) { var __cap = cond; if (Cond.B(__cap.HasValue)) { var x = __cap.Value; … } else break; }` (a niche optional pointer tests non-null / binds the pointer itself). A real loop, so `break`/`continue` (incl. the labeled forms) compose; `\|_\|` iterates without binding. **Deferred:** the `: (cont)` continue-expr capture-while, the while-`else` clause, and the error-union capture-while |
| `while (c) : (cont) …` | ✅ | the continue-expression → the C IR `for`-post, so `continue` runs `cont` (faithful to Zig). The common assignment cont `: (i = i + 1)` and a bare-expr cont both parse |
| `break;` / `continue;` | ✅ | unlabeled — reuse the C IR loop-control nodes |
| `break :blk v;` (labeled break with value) | ✅ | yields `v` from the enclosing labeled value-block (see **labeled block as a value** in Expressions) |
| `lbl: while/for (…) …` (labeled loop) + `break :lbl;` / `continue :lbl;` | ✅ | a `label:` may prefix any while/for loop; `break :lbl` / `continue :lbl` exit / next-iterate it — including an **outer** loop. C# has no labeled break/continue, so they lower to a `goto`: `break :lbl` → a label just AFTER the loop, `continue :lbl` → a label at the END of the loop body (so the natural iteration step still runs). Labels are emitted only when referenced. **Deferred:** the labeled-while/for VALUE form (`break :lbl v` yielding from a loop used as an expression) |
| `switch (x) { v => {…}, a, b => {…}, lo...hi => {…}, else => {…} }` | ✅ | as a STATEMENT → the C IR Switch. Single / multi-value / inclusive-**range** (`lo...hi`) / `else` (→ default) prongs; NO fall-through (each prong gets an appended `break`). Switching on an **enum** works (subject + `.member` labels decay to the underlying int). A range lowers to a C# relational-pattern case `case >= lo and <= hi:` (Zig requires comptime-known bounds = C#'s constant requirement). Prong bodies are braced **blocks** OR a bare expression (`v => expr`, an expression statement) |
| `switch (u) { .a => \|x\| {…}, .b => \|*y\| {…} }` | ✅ | switch on a **tagged union** → dispatch on the `__tag`; a `\|x\|` payload capture binds the matched variant's payload **by value**, and a by-reference `\|*x\|` capture (Milestone M, part 4) binds a `*T` into the payload field (`T* x = &u.__payload.v;`) so `x.* = …` writes through to the (mutable) union. An exhaustive union switch with no `else` makes its last prong the C# `default` (so the function provably returns). **Deferred:** multi-variant capture prongs (the optional `if`/`while` and the error-union `if` captures are now ✅ — see those rows) |
| `return e;` / `return;` | ✅ | |
| `x = e;` assignment | ✅ | |
| `x op= e;` compound assignment | ✅ | all ten: `+= -= *= /= %= <<= >>= &= \|= ^=`. → the shared `Assign` IR node with a non-null `CompoundOp` → a NATIVE C# `x op= e`, so the target lvalue is evaluated exactly **once** (`arr[next()] += 1` calls `next()` a single time — not a `x = x op e` desugar). Zig has the wrapping (`+%=`) / saturating (`+\|=`) variants (deferred) and **no** `++`/`--` (the idiom is `i += 1`) |
| `const a, const b = e;` destructure | ✅ | bind a tuple's elements to new locals (Milestone G) — desugars to a single-eval temp + per-element `.ItemN` reads (a brace-less sequence, so the binders stay in the enclosing scope). `const`/`var` binders, ≥2. **Deferred:** the assign-to-existing-lvalue form `a, b = e;` (a grammar-level cut, so a parse error) and typed binders (`const a: T, …`) |
| `_ = e;` discard | ✅ | Zig's mandatory discard of a non-void result |
| block `{ … }` | ✅ | |
| `defer Stmt;` | ✅ | scope-exit cleanup — runs on EVERY exit from the enclosing block (fall-through, `return`, `break`, `continue`, a propagating error), in LIFO declaration order. → C# `try { rest } finally { cleanup }`. The deferred `Stmt` is an `expr;`, a `_ = expr;` discard, or a braced block. See the **Defer** section |
| `errdefer Stmt;` | ✅ | error-exit cleanup — runs only when the block exits via a propagating error, LIFO-interleaved with `defer`. → C# `try { rest } catch (ZigErrorReturn) { cleanup; throw; }`. A function with an `errdefer` routes its `return error.X` through a throw so it reaches the catch. **NOT pursued:** `errdefer \|e\| …` payload capture — current Zig (0.17) has REMOVED that syntax (`errdefer \|e\|` is a parse error: "expected block or expression, found '\|'"), so dotcc rejects it too (round-trippable) |
| `for (a..b) \|i\| …` (range for) | ✅ | → C `for (usize i = a; i < b; i++)`; the `\|i\|` capture is the usize loop index (`\|_\|` discards). The body is a Stmt or block |
| `for (s) \|x\|` / `for (s, 0..) \|x, i\|` (for-over-slice) | ✅ | iterate a slice's elements (`x` = a per-iteration copy); the index form also binds the usize index. → C `for (usize __i=0; __i<s.Len; __i++) { var x = s.Ptr[__i]; [var i = __i + 0;] … }` (the slice is hoisted to a temp unless a bare var) |
| `for (s) \|*e\|` (by-reference for-slice) | ✅ | BY-REFERENCE element capture (Milestone M, part 4): `e` is a `T*` into the slice element (`T* e = &s.Ptr[__i];`), so `e.* = …` writes through to the element. **Deferred:** the by-ref + index combo `for (s, 0..) \|*e, i\|` |
| open-ended `s[lo..]` | 🚫 | (switch-as-expression, labeled-block-as-value, labeled loops + labeled `break`/`continue`, switch ranges, and by-ref `\|*x\|` capture in `for`/`switch` are now ✅; by-ref on an optional/error-union `if`/`while` stays a grammar-level cut) |

## Expressions

| Feature | Status | Notes |
|---|---|---|
| integer / float / string literals | ✅ | decimal **+ `0x`/`0o`/`0b` radix + `_` separators** (see Lexer); float incl. hex `0x1.8p3`; string reuses C escape decoding (`\n \t \\ \" \xNN`) **+ `\u{…}` + multiline `\\`** |
| `true` / `false` | ✅ | boolean literals — a `bool` value (→ C# `true`/`false`, stored in the normalising `CBool`) |
| char literal `'x'` | ✅ | Zig's `comptime_int` = the codepoint → an integer literal (`'A'` → 65). Escapes `\n \t \r \\ \' \xNN` + octal decode via the shared string-escape machinery, plus `\u{NNNN}` (decoded Zig-side). **Deferred:** a `\u{…}` codepoint > 0xFFFF (lowered as a single int — surrogate handling deferred) |
| identifiers, `(grouped)` | ✅ | |
| `or` `and` (short-circuit) | ✅ | |
| comparison `== != < > <= >=` | ✅ | non-associative (`a < b < c` is a parse error, like Zig) |
| bitwise `& ^ \|`, shift `<< >>` | ✅ | |
| arithmetic `+ - * / %` | ✅ | usual-arithmetic result typing (fixes i64 truncation) |
| prefix `-` `~` `!` | ✅ | |
| `if (c) a else b` (if-**expression**) | ✅ | → C# ternary |
| `switch (x) { v => e, … }` (switch-**expression**) | ✅ | a switch in value position (a typed binding / return / any `RhsExpr`) → C#'s native switch EXPRESSION (`x switch { v => e, a or b => e, _ => e }`). Each prong yields a value (a bare-expr body); `else` → the `_` default; arm values lower at the result sink; an enum subject + `.member` labels decay to the underlying int. Same structural trick as the if-expression (a `RhsExpr`, not a Primary). An inclusive **range** arm `lo...hi => e` lowers to a relational pattern `>= lo and <= hi => e`. **Deferred:** a block-bodied prong (needs a labeled `break :blk v`), a `\|x\|` capture in expression position |
| `blk: { …; break :blk v; }` (labeled block as a value) | ✅ | a block in value position that runs statements and YIELDS a value via `break :blk v`. → the roadmap's temp-fill: a result temp (`__blkN`), each `break :blk v` rewritten to `temp = v; goto __blkN_end;` (braced, so a conditional break stays conditional), an end label, then the surrounding statement reads the temp. The result type is the sink (an annotated decl / function return / lvalue) or the first `break` value's type. Same structural trick as the if/switch-expression (a `RhsExpr`, not a Primary). **Deferred** (clear errors): an if/switch-expression arm or a sub-expression position (only a full `=`/`return`/assignment RHS today), a global initializer (needs a comptime value), and an error-union (`!T`) function return |
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
| `a orelse b` (value RHS) | ✅ | value optional → C# `??` (single-eval, lazy `b`); pointer → `a != null ? a : b` (simple LHS) |
| `a orelse return [v]` / `a catch return [v]` (control-flow fallback) | ✅ | the unwrapped payload, or — on none / error — an EARLY `return` from the current function (Milestone N, part 6). Lowered structurally at a `const`/`var` initializer or a statement: hoist the operand, `if (none / error) { return …; }`, then bind the payload. The `return` wraps correctly in a `!T` fn (incl. `return error.X`). Both a value (`return v`) and void (`return`) form. (`a catch \|e\| return e` is just `try a` — use `try`.) **Deferred:** a sub-expression position; `break`/`continue` control-flow fallbacks |
| prefix `try` | ✅ | unwrap an error union's payload, or PROPAGATE its error out of the enclosing `!T` fn (exception-based early-return, modeled on the setjmp lowering). `try e;` as a statement works too |
| `e catch fallback` | ✅ | the payload on success, else the fallback. A simple, side-effect-free fallback keeps the eager `ErrUnion.Catch(a, b)`; a SIDE-EFFECTING fallback (Milestone N, part 3) lowers LAZILY — the union is hoisted to a single-eval temp and `b` runs only on error via a ternary `__cE.IsErr ? b : __cE.Value`. The lazy/capturing forms need a statement context (a `const`/`var` initializer); a side-effecting fallback in a sub-expression is still rejected |
| `e catch \|err\| fallback` (catch capture) | ✅ | binds the error to `err` for the fallback `b` (Milestone N, part 3), lowered lazily — hoist the union, bind `ushort err = __cE.Code;`, then `__cE.IsErr ? b : __cE.Value` so `b` (which may use `err`, e.g. `err == error.Bad`) runs only on error. As a `const`/`var` initializer. **Deferred:** the control-flow fallback `catch return` / `catch \|e\| return e` (clusters with `orelse return`); the capture in a sub-expression / statement position |
| `error.Foo` (bare error value) | ✅ | a first-class error VALUE (Milestone N, part 1) — usable outside `return error.Foo;`: bound to a `const`/`var`, captured (`else \|e\|` / future `catch \|e\|`), and compared. V1 erases the named set into one flat global code space, so an `error.Foo` lowers to its stable `ushort` code, typed `CType.ErrorSet`. (Explicit `error{A,B}` set decls / named `E!T` distinct from `anyerror!T` are still deferred) |
| `e == error.Foo` / `e != error.Foo` (error-value equality) | ✅ | error-value comparison (Milestone N, part 1) — equal codes mean equal errors, so `==`/`!=` lower to the ordinary integer comparison of the flat codes. Works on a bound error too (`else \|e\|` / a `const`), which un-erases the Milestone M part-3 cut (a USED named `\|e\|` is now valid in both compilers) |
| `switch (e) { error.Foo => …, else => … }` (error switch) | ✅ | switch on an error value (Milestone N, part 2) — an error value IS its flat `ushort` code, so this lowers to an ORDINARY integer `switch` on the code (each `error.Foo` prong → a `case <code>:`, `else` → `default:`). Rode in on part 1's representation — no new lowering. The error is commonly captured from `else \|e\|` first; an `anyerror!T` (open set) requires the `else` |
| `const E = error{ A, B };` (error-set declaration) | ✅ | an explicit named error set (Milestone N, part 5). dotcc erases the set into the flat global code space, so the decl is COMPTIME — it registers the member names (each a stable code) and emits NO runtime decl; `E` is then used only as the (ignored) set in an `E!T` return type, which lowers to the same `ErrUnion<T>` as `anyerror!T`. An inline `error{A}!T` return type works the same way. **Deferred:** `E.member` access (use the global `error.member`); `@errorName` (needs the un-erased name table) |
| postfix `.field` | ✅ | struct field access → the shared `Member` IR (field type from the aggregate table). Zig has no `->`, so `p.field` on a `*T` auto-derefs (emits C# `->`). `EnumName.member` resolves here too → an `EnumConstRef` |
| `.{ .f = v, … }` (anonymous struct literal) | ✅ | result-located → `new T { f = v }` from the sink type (a typed decl, return, assignment, call arg, or field). Empty `.{}` zero-inits |
| `T{ .f = v, … }` (typed struct literal) | ✅ | Zig's `CurlySuffixExpr <- TypeExpr InitList?` — the type is named, so NO sink is needed; valid in any position, incl. sink-less ones like `(T{…}).field`. A dedicated `CurlySuffix` grammar level (above `Type`) makes it conflict-free against `fn f() RetType {` (the return type stays a raw `Type`, no init list) — no rewriter. `&T{…}` (address of a temporary) materializes a block-local temp and takes its address (the same shared-backend path as C's `&(T){…}`) |
| `.{ a, b, … }` (positional tuple literal) | ✅ | result-located → `new System.ValueTuple<…>(a, b)` (Milestone G); element types come from a tuple sink, or are inferred from the elements (`const t = .{a, b};`). Shares the `.{…}` surface with the named struct literal — a literal that MIXES positional + named is rejected |
| `.enumLiteral` | ✅ | a bare `.member` resolves against its sink (typed decl / return / assignment / call arg / switch subject) → an `EnumConstRef` (`EnumName.member`). Untyped (no sink) is rejected, as Zig requires |
| `@intFromEnum(e)` | ✅ | the enum's integer value → decay to the underlying type (the C enum→int decay) |
| `@intCast` / `@truncate` / `@floatFromInt` / `@intFromFloat` / `@floatCast` / `@enumFromInt` / `@ptrCast` (x) | ✅ | **result-location** casts (Milestone J) — single-arg, target inferred from the SINK (typed binding / return / assignment / call arg / nested `@as`), not a type arg → the C `Cast` IR. Used with no result location they're a clear error, as Zig requires. The cast follows Zig's NON-safe-mode semantics (no overflow trap — same stance as plain `+`) |
| `@bitCast(x)` | ✅ | same-size **bit** reinterpret (e.g. `f32`↔`u32`) → `System.Runtime.CompilerServices.Unsafe.BitCast<TFrom, TTo>` (AOT-clean, size-checked). Result-located like the casts above |
| `@alignCast(p)` | ✅ | identity in dotcc's managed model (alignment is unobservable); the enclosing `@ptrCast`/sink does the real conversion. Needs no sink, so its idiomatic `@ptrCast(@alignCast(p))` lowers to one cast |
| `@sizeOf(T)` | ✅ | the byte size as `usize` → the C `sizeof` IR (folded for a user aggregate via the layout model, else C#'s `sizeof(T)`) |
| `@alignOf(T)` / `@offsetOf(T, f)` | 🚧 | parse only — alignment isn't meaningfully observable on the managed VM and `@offsetOf` waits on surfaced field offsets (deferred; revisit per-need) |
| other `@builtin(...)` (`@typeInfo`/`@TypeOf`/`@field`/…) | 🚫 | reflection / comptime — out of scope (see below) |
| wrapping/saturating ops | 🚫 | |

## Lexer

| Feature | Status |
|---|---|
| decimal integers, floats, `"…"` strings, char literals `'x'` (`\n \t \\ \' \xNN`), `//` line comments, `@name` builtins | ✅ |
| hex/octal/binary integers `0x1F`/`0o17`/`0b1010` + `_` digit separators `1_000_000` | ✅ | radix + `_` decoded in `DecodeZigInt` (Zig's `0o` octal / `_` separator, UNLIKE C's bare-`0` / `'`); the literal's carrier type is the narrowest of int/uint/long/ulong holding it |
| hex float `0x1.8p3` + underscored float `1_000.5` | ✅ | hex float has no C# syntax → converted to a round-trippable decimal via the shared `EmitHelpers.LowerHexFloat` |
| multiline `\\` strings | ✅ | a run of `\\`-prefixed lines folded into one literal, lines joined by `\n`; escapes are NOT processed (raw content), matching Zig |
| `\u{NNNN}` unicode escapes (string + char), escaped-quote `\"` in a string | ✅ | `\u{…}` expands to its UTF-8 bytes Zig-side (the shared decoder is untouched); `\"` is an escaped quote (the old `"[^"]*"` rule truncated there) |
| `\u{…}` with a codepoint > 0xFFFF, `1e10` (exponent-only, no point), `0X`/`0O`/`0B` (uppercase prefix) | 🚫 | non-BMP `\u{…}` in a char literal lowers as a single int (surrogate handling deferred); exponent-only float + uppercase radix prefix not lexed yet |

## Out of scope (the dialect line)

`comptime` (beyond const folding), generics / `anytype`, `@import("std")` beyond the curated
allocator paths (`std.mem.Allocator` + `std.heap.page_allocator`/`c_allocator`/
`FixedBufferAllocator` ARE supported — see the Allocators section), untagged
`union { … }` + explicit `union(SomeEnum)` + `opaque` (data-only `struct`/`enum`/`union`
**with methods** — struct/enum/union methods + the `const Self = @This();` self-type alias
+ namespaced VALUE `const`s ARE supported — see below), container-level `var` (a namespaced
mutable global — *top-level* `const`/`var` globals ARE now supported, but the container-level
`Type.field` wiring is not),
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

A bare `error.Foo` is now a first-class VALUE (Milestone N, part 1): the named set is still
erased, so an error value IS its flat `ushort` code (typed `CType.ErrorSet`), and error-value
equality `e == error.Foo` / `e != error.Foo` compares codes. This makes a `const`-bound error and
a USED `else |e|` capture usable (the latter un-erases the Milestone M part-3 cut — a named `|e|`
compared against an error is finally valid in both compilers). Because an error value is its code,
a `switch (e)` on an error (Milestone N, part 2) lowers to an ordinary integer `switch` on the code
(`error.Foo` → `case <code>:`, `else` → `default:`) — it rode in on the part-1 representation.

`catch` now supports a SIDE-EFFECTING fallback and a `catch |e|` capture (Milestone N, part 3):
both lower lazily (hoist the union to a single-eval temp, run the fallback only on error via a
ternary; the capture binds `e` to the flat error code, usable as `e == error.Foo`). These need a
statement context (a `const`/`var` initializer).

**V1 limits** (documented, not silent): the error SET is erased to one flat global code
space (`!T` / `anyerror!T` / `E!T` lower identically; an `error.Foo` value is its code); the
payload must be a value type (an error union over a *pointer* is deferred — a C# generic can't
take a pointer arg); a side-effecting / capturing `catch` is a `const`/`var` initializer only (a
sub-expression position keeps the eager-only rule). The control-flow fallbacks `catch return [v]` /
`orelse return [v]` (Milestone N part 6) ARE supported (a `const`/`var` initializer or a statement;
an early `return` on the error / none path); `catch |e| return e` is just `try` (use `try`). An
error-union `main`
(`!void` / `!u8`, Milestone N part 4) IS supported — an error from main reports its flat code to
stderr and exits 1 (real zig prints the error NAME + a trace; the name awaits the un-erased set).
Explicit `error{A, B}` set declarations (Milestone N part 5) are supported — dotcc erases the set,
so the decl is comptime (registers names, emits nothing) and `E!T` lowers like `anyerror!T`. STILL
un-erasing-bound: `@errorName(e)` and real error NAMES in a main trace need a runtime code→name
table (deferred). `errdefer |e|` capture is NOT pursued (current Zig removed the syntax).

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
  allocation with its release, plus an `errdefer` step that fires on the error path),
  `examples/zig-literals` (bool + char literals: `true`/`false` driving a branch, char-codepoint
  arithmetic for an ASCII case fold, the common escapes),
  `examples/zig-compound-assign` (compound assignment: `i += 1` as the `++` replacement, a
  `+=`/`-=` chain, single-eval on the target lvalue),
  `examples/zig-globals` (top-level globals: a typed `const`, a const initialized from an earlier
  const, and a mutable `var` accumulator bumped by a function through its bare name),
  `examples/zig-lexer` (lexer & literals: `0x`/`0o`/`0b` radix + `_` separators, a hex float, an
  escaped quote + `\u{…}` unicode escape, and a `\\` multiline string),
  `examples/zig-builtins` (result-location cast builtins: `@intCast`/`@truncate`/`@floatFromInt`/
  `@floatCast`/`@intFromFloat`/`@bitCast`/`@ptrCast`+`@alignCast`/`@enumFromInt` + `@sizeOf`,
  each inferring its target from the binding it flows into),
  `examples/zig-arrays` (array literals & aggregate globals: `.{…}` at a `[N]T` sink, typed
  `[N]T{…}` / inferred `[_]T{…}` locals, plus literal-array / inferred-array / `undefined`-array
  and struct globals routed through the pinned global store),
  `examples/zig-switch-expr` (switch as an expression: a return-position enum switch with
  `.member` labels + `else`, and a typed-decl int switch with a multi-value prong → C#'s native
  switch expression),
  `examples/zig-labeled-block` (labeled block as a value: typed-decl / inferred / return /
  assignment `blk: { …; break :blk v; }`, including a conditional `break :blk` from inside an `if`),
  `examples/zig-labeled-loop` (labeled loops: `break :outer` and `continue :scan` from nested loops
  → `goto` to a break label after the loop / a continue label at the body's end),
  `examples/zig-switch-range` (switch ranges: a char-classifier switch EXPRESSION `'0'...'9' => …`
  and a statement switch with `lo...hi` ranges + a multi-value prong → C# relational patterns),
  `examples/zig-if-capture` (optional payload capture in `if`: value optional then/else/`_`/no-else
  + a niche optional-pointer capture written through),
  `examples/zig-while-capture` (optional capture-`while`: a value-optional iterator-style loop
  `while (nextLT(&i, 9)) |v|` + a `_` discard capture-while),
  `examples/zig-error-capture` (error-union capture in `if`: payload `|x|` on success + the error
  branch on failure via `else |_|`),
  `examples/zig-byref-capture` (by-reference capture `|*x|`: doubling a slice in place via
  `for (s) |*e|` + mutating a tagged-union payload via `switch (b) { .i => |*p| … }`),
  `examples/zig-error-value` (error values as comparable values: a USED `else |e|` capture compared
  against `error.Bad`, plus a `const`-bound bare error value tested with `==`/`!=`),
  `examples/zig-error-switch` (`switch` on an error value: a captured `else |e|` switched over
  `error.Zero` / `error.Negative` / `else` → an integer switch on the flat code),
  `examples/zig-catch-capture` (`catch |e|` capture using `e == error.Bad` for a bool fallback, plus
  a lazy side-effecting `catch dflt()` whose call runs only on the error path),
  `examples/zig-errunion-main` (error-union `main`: `pub fn main() !u8` with `try` inside, the payload
  as the process exit code; an error would propagate to the entry and exit 1),
  `examples/zig-error-set` (a named `const MathError = error{ Overflow, Negative };` used as a
  `MathError!i32` return type — the erased set, members returned via `error.X` and handled with `catch`),
  `examples/zig-catch-orelse-return` (control-flow fallbacks: `mk(a) catch return error.NoX` (error
  union early-return) + `pick(b) orelse return 0` (optional early-return) inside a `!i32` function).
