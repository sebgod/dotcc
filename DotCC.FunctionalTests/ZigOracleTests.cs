#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// Opt-in differential test for the Zig front-end: compile + run a Zig program
/// through dotcc (emit C# → Roslyn → run) AND through the real <c>zig</c>
/// compiler, then assert they agree. The Zig analogue of
/// <see cref="GccWslOracleTests"/> / <see cref="MsvcOracleTests"/>, but a PURE
/// differential — there is no committed snapshot to validate. The always-on
/// <see cref="ZigFrontendTests"/> already pins dotcc's emit; here real zig IS
/// the oracle, so the two pipelines are compared head-to-head with no baseline
/// file in between.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exit code is the observable</b> — the current Zig surface has no I/O, so
/// each case compares the <b>process exit code</b> (the program's <c>main</c>
/// return; Zig's <c>fn main() u8</c> returns the exit code), which is why
/// <see cref="FixtureRunner.CompileAndRunCapturingExit"/> exists. Once
/// <c>@cImport</c> brings <c>c.printf</c> → stdout, these grow a stdout
/// differential and a <c>ZigFixtures/&lt;name&gt;/</c> walk mirroring
/// <see cref="FixtureRunner.Discover"/>. For now the cases are inline.
/// </para>
/// <para>
/// <b>Modes</b> (env vars): opt-in via <c>DOTCC_RUN_ZIG_ORACLE=1</c> (skips with
/// a hint otherwise); skips with a clear message when no <c>zig</c> is on PATH.
/// Same posture as the gcc/MSVC oracles — toolchain absence is a skip.
/// </para>
/// </remarks>
public sealed class ZigOracleTests
{
    private const string RunZigEnv = "DOTCC_RUN_ZIG_ORACLE";

    private static bool ZigRunRequested =>
        Environment.GetEnvironmentVariable(RunZigEnv) == "1";

    /// <summary>Each case: a self-contained Zig program + its expected process exit
    /// code + expected stdout (newline-normalized, trailing-newline-trimmed). They span
    /// the lowered surface — arithmetic, comparison, the if-expression, if/while
    /// statements + assignment, a prefix op, parameters, a function call (incl. forward
    /// reference), and an `extern fn` libc call that produces real OUTPUT — so a
    /// divergence pins which feature drifted from real zig.</summary>
    public static IEnumerable<object[]> Programs => new[]
    {
        new object[] { "arith",
            "pub fn main() u8 { const x: u8 = 40; return x + 2; }\n", 42, "" },
        new object[] { "if_expr",
            "pub fn main() u8 { const x: u8 = 40; const y: u8 = if (x > 10) x else 0; return y + 2; }\n", 42, "" },
        new object[] { "if_stmt",
            "pub fn main() u8 { var x: u8 = 0; if (3 > 2) { x = 42; } else { x = 1; } return x; }\n", 42, "" },
        new object[] { "while_sum",
            "pub fn main() u8 { var i: u8 = 0; var sum: u8 = 0; while (i < 5) { sum = sum + i; i = i + 1; } return sum; }\n", 10, "" },
        new object[] { "bitnot",
            "pub fn main() u8 { const a: u8 = 0; const b: u8 = ~a; return b; }\n", 255, "" },
        // i64 parameters: `wide` is type-checked by dotcc's emit + Roslyn with the
        // wider signedness the UsualArithmetic fix preserves; main is the observable.
        new object[] { "i64_params",
            "fn wide(a: i64, b: i64) i64 { return a * b; }\npub fn main() u8 { return 42; }\n", 42, "" },
        // A function CALL — main invokes a named function with arguments.
        new object[] { "call",
            "fn add(a: u8, b: u8) u8 { return a + b; }\npub fn main() u8 { return add(40, 2); }\n", 42, "" },
        // A FORWARD-referenced call — `add` is defined AFTER `main` (Zig has no
        // prototypes); the two-pass lowering must resolve it.
        new object[] { "call_forward",
            "pub fn main() u8 { return add(40, 2); }\nfn add(a: u8, b: u8) u8 { return a + b; }\n", 42, "" },
        // extern fn libc FFI: `putchar` from libc (linked -lc) produces real STDOUT.
        // dotcc routes it by bare name to its Libc runtime; zig links the real libc.
        new object[] { "extern_putchar",
            "extern fn putchar(c: c_int) c_int;\npub fn main() u8 { _ = putchar(72); _ = putchar(105); _ = putchar(10); return 0; }\n", 0, "Hi" },
        // VARIADIC extern fn + a string literal: `printf` with `[*c]const u8` format
        // and a `...` pack. dotcc routes it through the printf-family fluent builder;
        // zig links real libc printf. The `%d` exercises the variadic-tail formatting.
        // The `@as(c_int, …)` cast is REQUIRED: a bare literal has no fixed-size ABI
        // type, so both zig AND dotcc reject `printf("%d", 42)` (variadic strictness).
        new object[] { "printf_fmt",
            "extern fn printf(format: [*c]const u8, ...) c_int;\npub fn main() u8 { _ = printf(\"Hi %d\\n\", @as(c_int, 42)); return 0; }\n", 0, "Hi 42" },
        // VOID-returning main (`pub fn main() void`) — idiomatic Zig with no exit code.
        // dotcc's shell calls it for effect and returns 0; real zig's start code does
        // the same. No explicit `return;` needed (a void body falls off the end).
        new object[] { "void_main",
            "extern fn printf(format: [*c]const u8, ...) c_int;\npub fn main() void { _ = printf(\"void %d\\n\", @as(c_int, 7)); }\n", 0, "void 7" },
        // OPTIONALS (Milestone B1). A `?*T` lowers to a bare nullable pointer (Zig's
        // niche); `null` is none, `orelse` defaults, `.?`/deref unwraps.
        new object[] { "optional_ptr",
            "pub fn main() u8 { var x: u8 = 5; const p: ?*u8 = &x; const q: ?*u8 = null; return (p orelse &x).* + (q orelse &x).*; }\n", 10, "" },
        // A `?T` over a value type → C# Nullable<T>: `orelse` is `??`, `.?` is `.Value`.
        new object[] { "optional_value",
            "pub fn main() u8 { const a: ?u8 = 40; const b: ?u8 = null; return (a orelse 0) + (b orelse 2); }\n", 42, "" },
        // ERROR UNIONS (Milestone B2). A `!u8` returns an error union; `try` unwraps-or-
        // propagates, `catch` supplies a fallback, `return error.X` is the error path.
        // try success: parse(40)=41, outer unwraps + adds → Ok(42), `catch 0` passes it through.
        new object[] { "errunion_try_ok",
            "fn parse(x: u8) !u8 { if (x == 0) return error.Zero; return x + 1; }\n" +
            "fn outer(x: u8) !u8 { const v = try parse(x); return v + 1; }\n" +
            "pub fn main() u8 { return outer(40) catch 0; }\n", 42, "" },
        // catch on the error path: parse(0) → error.Zero, so `catch 7` yields the fallback.
        new object[] { "errunion_catch_err",
            "fn parse(x: u8) !u8 { if (x == 0) return error.Zero; return x + 1; }\n" +
            "pub fn main() u8 { return parse(0) catch 7; }\n", 7, "" },
        // try PROPAGATION: parse(0) errors, `try` aborts `outer` with it (the exception-based
        // early return), and main's `catch 5` handles the propagated error → 5.
        new object[] { "errunion_propagate",
            "fn parse(x: u8) !u8 { if (x == 0) return error.Zero; return x + 1; }\n" +
            "fn outer(x: u8) !u8 { const v = try parse(x); return v + 1; }\n" +
            "pub fn main() u8 { return outer(0) catch 5; }\n", 5, "" },
        // `!void`: check() returns no payload; `try check(x);` propagates any error and
        // discards the void success. check(5) is fine → run returns Ok(9) → `catch 0` → 9.
        new object[] { "errunion_void",
            "fn check(x: u8) !void { if (x == 0) return error.Zero; }\n" +
            "fn run(x: u8) !u8 { try check(x); return 9; }\n" +
            "pub fn main() u8 { return run(5) catch 0; }\n", 9, "" },
        // CONTROL FLOW (Milestone C1). `while (cond) : (cont)` + `break` + `continue`.
        // i: 0,1,2,(skip 3),4,5,6,(break at 7) → sum = 0+1+2+4+5+6 = 18. The cont (`i = i+1`)
        // runs on `continue` too, so i still advances past 3.
        new object[] { "loop_break_continue",
            "pub fn main() u8 { var sum: u8 = 0; var i: u8 = 0; " +
            "while (i < 10) : (i = i + 1) { if (i == 3) continue; if (i == 7) break; sum = sum + i; } " +
            "return sum; }\n", 18, "" },
        // A plain `while : (cont)` with no break/continue — counts 0..4 → 0+1+2+3+4 = 10.
        new object[] { "loop_while_cont",
            "pub fn main() u8 { var sum: u8 = 0; var i: u8 = 0; " +
            "while (i < 5) : (i = i + 1) { sum = sum + i; } return sum; }\n", 10, "" },
        // SWITCH (Milestone C2). Single / multi-value / else prongs, no fall-through.
        // classify(2) hits the `1, 2` multi-value prong → 20.
        new object[] { "switch_multi",
            "fn classify(x: u8) u8 { var r: u8 = 0; switch (x) { " +
            "0 => { r = 10; }, 1, 2 => { r = 20; }, else => { r = 30; }, } return r; }\n" +
            "pub fn main() u8 { return classify(2); }\n", 20, "" },
        // classify(9) falls to `else` → 30. (Distinct exit code locks the default branch.)
        new object[] { "switch_else",
            "fn classify(x: u8) u8 { var r: u8 = 0; switch (x) { " +
            "0 => { r = 10; }, 1, 2 => { r = 20; }, else => { r = 30; }, } return r; }\n" +
            "pub fn main() u8 { return classify(9); }\n", 30, "" },
        // RANGE FOR (Milestone C3). `for (0..n) |i|` — the usize loop index used in a
        // comparison (narrowing it to u8 needs @intCast, deferred). i hits 7 → found = 42.
        new object[] { "for_range_index",
            "pub fn main() u8 { var found: u8 = 0; for (0..10) |i| { if (i == 7) { found = 42; } } return found; }\n", 42, "" },
        // The `|_|` discard form — count 5 iterations into a u8 (no usize arithmetic). → 5.
        new object[] { "for_range_count",
            "pub fn main() u8 { var sum: u8 = 0; for (0..5) |_| { sum = sum + 1; } return sum; }\n", 5, "" },
        // STRUCTS (Milestone D1). A `struct` decl + a result-located `.{…}` literal +
        // field reads — built on the SAME C# struct machinery the C frontend uses.
        new object[] { "struct_field",
            "const Point = struct { x: u8, y: u8 };\n" +
            "pub fn main() u8 { const p: Point = .{ .x = 40, .y = 2 }; return p.x + p.y; }\n", 42, "" },
        // A `*Point` parameter + `p.x` (Zig auto-derefs a pointer field access → C# `->`).
        new object[] { "struct_ptr",
            "const Point = struct { x: u8, y: u8 };\n" +
            "fn sum(p: *Point) u8 { return p.x + p.y; }\n" +
            "pub fn main() u8 { var pt: Point = .{ .x = 30, .y = 12 }; return sum(&pt); }\n", 42, "" },
        // ENUMS (Milestone D1). A typed `enum(u8)` + a sink-typed `.blue` literal +
        // `@intFromEnum` (the enum→int decay). blue = 2 → 2 + 40 = 42.
        new object[] { "enum_value",
            "const Color = enum(u8) { red, green, blue };\n" +
            "pub fn main() u8 { const c: Color = .blue; return @intFromEnum(c) + 40; }\n", 42, "" },
        // Explicit member value + auto-increment continuation: a = 40, b = 41, c = 42.
        new object[] { "enum_explicit",
            "const E = enum(u8) { a = 40, b, c };\n" +
            "pub fn main() u8 { return @intFromEnum(E.c); }\n", 42, "" },
        // `switch` on an enum with dotted `.member` cases (subject + labels decay to the
        // underlying int). `else` makes it exhaustive. rank(.green) → 42.
        new object[] { "enum_switch",
            "const Color = enum { red, green, blue };\n" +
            "fn rank(c: Color) u8 { switch (c) { .red => { return 1; }, .green => { return 42; }, else => { return 3; }, } }\n" +
            "pub fn main() u8 { return rank(.green); }\n", 42, "" },
        // TYPED struct literal `Point{ … }` (Zig's CurlySuffixExpr). Unlike the anonymous
        // `.{…}`, it names its own type → no sink needed → valid in a sink-less position like
        // an immediate field access `(Point{…}).y`. 40 + 2 - 9 + 9 = 42.
        new object[] { "struct_typed_literal",
            "const Point = struct { x: u8, y: u8 };\n" +
            "pub fn main() u8 {\n" +
            "    const p = Point{ .x = 40, .y = 2 };\n" +
            "    const j = (Point{ .x = 5, .y = 9 }).y;\n" +
            "    return p.x + p.y - 9 + j; }\n", 42, "" },
        // `&T{…}` — address of a temporary, passed as a `*const Point` arg (in Zig `&literal`
        // is `*const T`). C# can't take `&new T{…}`, so the literal is materialized to a
        // block-local temp and its address taken (shared with C's `&(T){…}`).
        new object[] { "struct_addr_literal",
            "const Point = struct { x: u8, y: u8 };\n" +
            "fn sum(p: *const Point) u8 { return p.x + p.y; }\n" +
            "pub fn main() u8 { return sum(&Point{ .x = 40, .y = 2 }); }\n", 42, "" },
        // METHODS / UFCS (Milestone D2). A struct body holds methods alongside fields, each
        // lowered to a mangled free function `Point_method`. Exercises all three call forms:
        // a static/associated function (`Point.init`), a pointer-receiver method that mutates
        // (`p.scale(2)` → auto-ref `&p`, `self->x`), and a value-receiver method whose receiver
        // type is `@This()` (`p.sum()`). init(20,1) → scale(2) ⇒ {40,2} → sum ⇒ 42.
        new object[] { "struct_methods",
            "const Point = struct {\n" +
            "    x: u8,\n" +
            "    y: u8,\n" +
            "    fn init(x: u8, y: u8) Point { return .{ .x = x, .y = y }; }\n" +
            "    fn scale(self: *Point, f: u8) void { self.x = self.x * f; self.y = self.y * f; }\n" +
            "    fn sum(self: @This()) u8 { return self.x + self.y; }\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    var p = Point.init(20, 1);\n" +
            "    p.scale(2);\n" +
            "    return p.sum();\n" +
            "}\n", 42, "" },
        // SELF-TYPE ALIAS (D2 follow-up). `const Self = @This();` — the ubiquitous Zig idiom —
        // names the container type inside its own methods. Used as a static-call base
        // (`Self.init`), a return type, a `Self{…}` literal, and a value-receiver param type
        // (`self: Self`). init(40,2) → sum ⇒ 42.
        new object[] { "self_alias",
            "const Vec = struct {\n" +
            "    a: u8,\n" +
            "    b: u8,\n" +
            "    const Self = @This();\n" +
            "    fn init(a: u8, b: u8) Self { return Self{ .a = a, .b = b }; }\n" +
            "    fn sum(self: Self) u8 { return self.a + self.b; }\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    const v = Vec.init(40, 2);\n" +
            "    return v.sum();\n" +
            "}\n", 42, "" },
        // ENUM METHODS (D2/D3 leftover). An enum body holds methods alongside value members, each
        // a mangled free function `Color_method` with the enum value as the receiver. `self == .red`
        // result-locates the bare `.member`; `@intFromEnum(self)` decays to the underlying int.
        // isRed(blue)=false → rank(blue)=2, +40 ⇒ 42.
        new object[] { "enum_methods",
            "const Color = enum(u8) {\n" +
            "    red,\n" +
            "    green,\n" +
            "    blue,\n" +
            "    fn isRed(self: Color) bool { return self == .red; }\n" +
            "    fn rank(self: Color) u8 { return @intFromEnum(self); }\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    const c: Color = .blue;\n" +
            "    if (c.isRed()) { return 1; }\n" +
            "    return c.rank() + 40;\n" +
            "}\n", 42, "" },
        // UNION METHODS (D2/D3 leftover). A `union(enum)` body holds methods; the method body
        // switches on the receiver with `|capture|` payload binding. value(circle 40)=42,
        // value(none)=0 ⇒ 42.
        new object[] { "union_methods",
            "const Shape = union(enum) {\n" +
            "    circle: u8,\n" +
            "    square: u8,\n" +
            "    none,\n" +
            "    fn value(self: Shape) u8 {\n" +
            "        switch (self) {\n" +
            "            .circle => |r| { return r + 2; },\n" +
            "            .square => |x| { return x * x; },\n" +
            "            .none => { return 0; },\n" +
            "        }\n" +
            "    }\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    const a = Shape{ .circle = 40 };\n" +
            "    const b: Shape = .none;\n" +
            "    return a.value() + b.value();\n" +
            "}\n", 42, "" },
        // NAMESPACED VALUE CONSTS (D2/D3 leftover). A container-level `const NAME = expr;` is a
        // comptime constant read as `Type.NAME`; dotcc inlines the RHS. A struct const literal
        // (`Cfg.max`=40) + an enum const whose value is an enum member (`Color.fallback`=blue=2),
        // 40 + 2 ⇒ 42.
        new object[] { "namespaced_const",
            "const Cfg = struct {\n" +
            "    pub const max: u8 = 40;\n" +
            "};\n" +
            "const Color = enum(u8) {\n" +
            "    red, green, blue,\n" +
            "    pub const fallback = Color.blue;\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    return Cfg.max + @intFromEnum(Color.fallback);\n" +
            "}\n", 42, "" },
        // TAGGED UNIONS (Milestone D3). `union(enum)` → a discriminated struct (tag enum +
        // `__tag` + payload fields). Exercises payload construction (`Shape{ .circle = 40 }`),
        // a void variant (`.none`), and a `switch` with `|r|` payload capture. value(circle 40)
        // = 42, value(none) = 0 ⇒ 42.
        new object[] { "union_tagged",
            "const Shape = union(enum) {\n" +
            "    circle: u8,\n" +
            "    square: u8,\n" +
            "    none,\n" +
            "};\n" +
            "fn value(s: Shape) u8 {\n" +
            "    switch (s) {\n" +
            "        .circle => |r| { return r + 2; },\n" +
            "        .square => |x| { return x * x; },\n" +
            "        .none => { return 0; },\n" +
            "    }\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const a = Shape{ .circle = 40 };\n" +
            "    const b: Shape = .none;\n" +
            "    return value(a) + value(b);\n" +
            "}\n", 42, "" },
        // SLICES (Milestone E, stage 1). `[]const u8` params, `.len`, element index `s[i]`,
        // and the array→slice coercion (a string literal `*const [N:0]u8` → `[]const u8`,
        // its `.len` excluding the sentinel NUL). lenOf("hello")==5 and firstByte=='h'(104) → 42.
        new object[] { "slices",
            "fn lenOf(s: []const u8) usize { return s.len; }\n" +
            "fn firstByte(s: []const u8) u8 { return s[0]; }\n" +
            "pub fn main() u8 {\n" +
            "    const s: []const u8 = \"hello\";\n" +
            "    if (lenOf(s) == 5) { if (firstByte(s) == 104) { return 42; } }\n" +
            "    return 0;\n" +
            "}\n", 42, "" },
        // SLICING OPERATOR (Milestone E, stage 2). `s[lo..hi]` → a sub-slice `{ s.ptr+lo,
        // hi-lo }`. mid = "hello"[1..4] = "ell" (len 3, mid[0]=='e'==101) → 42.
        new object[] { "slice_range",
            "fn firstByte(s: []const u8) u8 { return s[0]; }\n" +
            "pub fn main() u8 {\n" +
            "    const s: []const u8 = \"hello\";\n" +
            "    const mid = s[1..4];\n" +
            "    if (mid.len == 3) { if (firstByte(mid) == 101) { return 42; } }\n" +
            "    return 0;\n" +
            "}\n", 42, "" },
        // FOR-OVER-SLICE (Milestone E, stage 3). `for (s) |b|` iterates elements;
        // `for (s, 0..) |b, i|` also binds the usize index. "hello" has two 'l' (108) at
        // indices 2 and 3 → countL == 2 and sumLpos == 5 → 42.
        new object[] { "for_slice",
            "fn countL(s: []const u8) usize { var n: usize = 0; for (s) |b| { if (b == 108) { n = n + 1; } } return n; }\n" +
            "fn sumLpos(s: []const u8) usize { var acc: usize = 0; for (s, 0..) |b, i| { if (b == 108) { acc = acc + i; } } return acc; }\n" +
            "pub fn main() u8 {\n" +
            "    const s: []const u8 = \"hello\";\n" +
            "    if (countL(s) == 2) { if (sumLpos(s) == 5) { return 42; } }\n" +
            "    return 0;\n" +
            "}\n", 42, "" },
        // ARRAY LOCALS (Milestone E follow-up). `var b: [N]T = undefined;` → a stackalloc'd
        // C array (zero heap); slicing it (`b[0..3]`) gives a stack-backed slice. Fill 10/20/12,
        // sum the slice → 42.
        new object[] { "array_local",
            "pub fn main() u8 {\n" +
            "    var buf: [4]u8 = undefined;\n" +
            "    buf[0] = 10; buf[1] = 20; buf[2] = 12;\n" +
            "    const s: []u8 = buf[0..3];\n" +
            "    return s[0] + s[1] + s[2];\n" +
            "}\n", 42, "" },
        // ALLOCATORS (Milestone F). The `run() catch 1` wrapper avoids the deferred `catch
        // return` (V1 cut): a `!u8` helper uses `try`, and main supplies a literal fallback.
        // alloc_page — the statically-known default DEVIRTUALIZES to a direct Libc.malloc/free.
        new object[] { "alloc_page",
            "const std = @import(\"std\");\n" +
            "fn run() !u8 {\n" +
            "    const a = std.heap.page_allocator;\n" +
            "    const buf = try a.alloc(u8, 4);\n" +
            "    buf[0] = 42;\n" +
            "    const r = buf[0];\n" +
            "    a.free(buf);\n" +
            "    return r;\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n", 42, "" },
        // alloc_fba — a FixedBufferAllocator (the 2nd allocator); `.alloc` dispatches INDIRECTLY
        // through the runtime Allocator vtable.
        new object[] { "alloc_fba",
            "const std = @import(\"std\");\n" +
            "fn run() !u8 {\n" +
            "    var buffer: [64]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.init(&buffer);\n" +
            "    const a = fba.allocator();\n" +
            "    const s = try a.alloc(u8, 3);\n" +
            "    s[0] = 10; s[1] = 15; s[2] = 17;\n" +
            "    return s[0] + s[1] + s[2];\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n", 42, "" },
        // alloc_param — an opaque `std.mem.Allocator` parameter (→ indirect dispatch), fed BOTH a
        // FixedBufferAllocator AND the default (which materializes through the same opaque path).
        new object[] { "alloc_param",
            "const std = @import(\"std\");\n" +
            "fn fill(a: std.mem.Allocator, n: usize) ![]u8 {\n" +
            "    const s = try a.alloc(u8, n);\n" +
            "    var i: usize = 0;\n" +
            "    while (i < n) : (i = i + 1) { s[i] = 7; }\n" +
            "    return s;\n" +
            "}\n" +
            "fn run() !u8 {\n" +
            "    var buffer: [64]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.init(&buffer);\n" +
            "    const s1 = try fill(fba.allocator(), 3);\n" +
            "    const s2 = try fill(std.heap.page_allocator, 3);\n" +
            "    return s1[0] + s1[1] + s1[2] + s2[0] + s2[1] + s2[2];\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n", 42, "" },
        // alloc_oom — a 4-byte FixedBufferAllocator can't satisfy a 100-byte request → the error
        // propagates through `try` and main's `catch 42` (the deterministic OOM error path).
        new object[] { "alloc_oom",
            "const std = @import(\"std\");\n" +
            "fn run() !u8 {\n" +
            "    var buffer: [4]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.init(&buffer);\n" +
            "    const a = fba.allocator();\n" +
            "    const s = try a.alloc(u8, 100);\n" +
            "    return s[0];\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 42; }\n", 42, "" },
        // TUPLES (Milestone G). The headline use: a function returns a tuple `struct { u8, u8 }`
        // and the caller destructures it with `const a, const b = mm();` → C# ValueTuple +
        // `.Item1`/`.Item2`. 20 + 22 = 42.
        new object[] { "tuple_return",
            "fn mm() struct { u8, u8 } { return .{ 20, 22 }; }\n" +
            "pub fn main() u8 { const a, const b = mm(); return a + b; }\n", 42, "" },
        // A tuple LITERAL bound to a var, indexed by literal subscript (`t[0]`/`t[1]` → `.ItemN`).
        new object[] { "tuple_index",
            "pub fn main() u8 { const t = .{ @as(u8, 20), @as(u8, 22) }; return t[0] + t[1]; }\n", 42, "" },
        // Destructure straight from an inline positional literal (its tuple type is inferred).
        new object[] { "tuple_destructure_literal",
            "pub fn main() u8 { const a, const b = .{ @as(u8, 40), @as(u8, 2) }; return a + b; }\n", 42, "" },
        // Arity 3 — a 3-tuple return + 3-binder destructure. 10 + 15 + 17 = 42.
        new object[] { "tuple_three",
            "fn mm() struct { u8, u8, u8 } { return .{ 10, 15, 17 }; }\n" +
            "pub fn main() u8 { const a, const b, const c = mm(); return a + b + c; }\n", 42, "" },
        // A tuple TYPE as a function PARAMETER, fed an inline literal at the call (result-located),
        // and indexed inside. 40 + 2 = 42.
        new object[] { "tuple_param",
            "fn sum(t: struct { u8, u8 }) u8 { return t[0] + t[1]; }\n" +
            "pub fn main() u8 { return sum(.{ 40, 2 }); }\n", 42, "" },
    };

    private static string Norm(string s) => s.ReplaceLineEndings("\n").TrimEnd('\n');

    [Theory]
    [MemberData(nameof(Programs))]
    public void Dotcc_matches_zig(string name, string program, int expectedExit, string expectedStdout)
    {
        if (!ZigRunRequested)
        {
            Assert.Skip(
                $"Zig oracle is opt-in. Set {RunZigEnv}=1 to compile + run each program " +
                $"with the real zig compiler and assert dotcc's Zig path agrees. The " +
                $"always-on ZigFrontendTests already pins dotcc's emit.");
        }
        if (!ZigOracle.IsAvailable)
        {
            Assert.Skip($"{RunZigEnv} requested but no `zig` is on PATH on this host.");
        }

        // dotcc path: write a temp .zig, emit C# (csproj-shaped — Roslyn rejects
        // the #:property header), compile in-process, run, capture the exit code.
        var zigPath = Path.Combine(Path.GetTempPath(), $"dotcc-zig-oracle-{name}-{Guid.NewGuid():N}.zig");
        File.WriteAllText(zigPath, program);
        int dotccExit;
        string dotccStdout;
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { zigPath }, fileBased: false);
            (dotccStdout, dotccExit) = FixtureRunner.CompileAndRunCapturingExit(emitted, Array.Empty<string>());
        }
        finally { File.Delete(zigPath); }

        // zig path: build + run the SAME source with the real compiler.
        var workDir = Path.Combine(Path.GetTempPath(), $"dotcc-zig-oracle-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var zigSrc = Path.Combine(workDir, "main.zig");
        File.WriteAllText(zigSrc, program);
        try
        {
            var (zigStdout, zigExit) = ZigOracle.CompileAndRun(zigSrc, workDir);

            // Both observables — exit code AND stdout — must agree with real zig.
            dotccExit.ShouldBe(zigExit, $"dotcc's Zig path diverges from real zig on '{name}' (exit code)");
            dotccExit.ShouldBe(expectedExit, $"'{name}' did not produce the expected exit code");
            Norm(dotccStdout).ShouldBe(Norm(zigStdout), $"dotcc's Zig path diverges from real zig on '{name}' (stdout)");
            Norm(dotccStdout).ShouldBe(expectedStdout, $"'{name}' did not produce the expected stdout");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
