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
        // Function-pointer types + anyopaque (Milestone W, part 1a): two ops share the signature
        // `fn (ctx: *anyopaque, by: i32) i32`, each treating its opaque ctx as a `*i32` accumulator
        // (the C void*-callback idiom). main binds each to a `*const fn (…) i32` value and calls it
        // INDIRECTLY. acc: 5 --bump 16--> 21 --scale 2--> 42.
        new object[] { "fn_ptr",
            "fn bump(ctx: *anyopaque, by: i32) i32 { const p: *i32 = @ptrCast(@alignCast(ctx)); p.* = p.* + by; return p.*; }\n" +
            "fn scale(ctx: *anyopaque, by: i32) i32 { const p: *i32 = @ptrCast(@alignCast(ctx)); p.* = p.* * by; return p.*; }\n" +
            "pub fn main() u8 {\n" +
            "    var acc: i32 = 5;\n" +
            "    const f1: *const fn (ctx: *anyopaque, by: i32) i32 = bump;\n" +
            "    const f2: *const fn (ctx: *anyopaque, by: i32) i32 = scale;\n" +
            "    _ = f1(&acc, 16);\n" +
            "    _ = f2(&acc, 2);\n" +
            "    return @intCast(acc);\n" +
            "}\n", 42, "" },
        // A user-constructed custom std.mem.Allocator (Milestone W, part 1b): a hand-written bump
        // allocator whose state lives behind the opaque ctx, bound to the real 4-fn VTable
        // (alloc/resize/remap/free, each carrying std.mem.Alignment + []u8 + ret_addr). main builds
        // `std.mem.Allocator{ .ptr = &state, .vtable = &bump_vtable }` and uses the standard alloc/free
        // surface; dispatch flows through the vtable. alloc 10, fill 0..9 (sum 45), free → 45 - 3 = 42.
        new object[] { "custom_allocator",
            "const std = @import(\"std\");\n" +
            "const Bump = struct { base: [*]u8, cap: usize, used: usize };\n" +
            "fn bumpAlloc(ctx: *anyopaque, len: usize, alignment: std.mem.Alignment, ret_addr: usize) ?[*]u8 {\n" +
            "    _ = alignment; _ = ret_addr;\n" +
            "    const self: *Bump = @ptrCast(@alignCast(ctx));\n" +
            "    if (self.used + len > self.cap) return null;\n" +
            "    const p = self.base + self.used;\n" +
            "    self.used += len;\n" +
            "    return p;\n" +
            "}\n" +
            "fn bumpResize(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ret_addr: usize) bool {\n" +
            "    _ = ctx; _ = memory; _ = alignment; _ = new_len; _ = ret_addr; return false;\n" +
            "}\n" +
            "fn bumpRemap(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ret_addr: usize) ?[*]u8 {\n" +
            "    _ = ctx; _ = memory; _ = alignment; _ = new_len; _ = ret_addr; return null;\n" +
            "}\n" +
            "fn bumpFree(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ret_addr: usize) void {\n" +
            "    _ = ctx; _ = memory; _ = alignment; _ = ret_addr;\n" +
            "}\n" +
            "const bump_vtable = std.mem.Allocator.VTable{ .alloc = bumpAlloc, .resize = bumpResize, .remap = bumpRemap, .free = bumpFree };\n" +
            "pub fn main() u8 {\n" +
            "    var backing: [256]u8 = undefined;\n" +
            "    var state = Bump{ .base = backing[0..].ptr, .cap = backing.len, .used = 0 };\n" +
            "    const a = std.mem.Allocator{ .ptr = &state, .vtable = &bump_vtable };\n" +
            "    const buf = a.alloc(u8, 10) catch return 1;\n" +
            "    var i: usize = 0;\n" +
            "    while (i < 10) : (i = i + 1) { buf[i] = @intCast(i); }\n" +
            "    var sum: u32 = 0;\n" +
            "    i = 0;\n" +
            "    while (i < 10) : (i = i + 1) { sum += buf[i]; }\n" +
            "    a.free(buf);\n" +
            "    return @intCast(sum - 3);\n" +
            "}\n", 42, "" },
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
        // create_cheap (Milestone U) — single-object alloc on the statically-known default
        // DEVIRTUALIZES to a direct malloc/free. Builds a two-node list, sums it, tears it down.
        new object[] { "create_cheap",
            "const std = @import(\"std\");\n" +
            "const Node = struct { value: u8, next: ?*Node };\n" +
            "fn run() !u8 {\n" +
            "    const a = std.heap.page_allocator;\n" +
            "    const head = try a.create(Node);\n" +
            "    head.value = 30;\n" +
            "    const tail = try a.create(Node);\n" +
            "    tail.value = 12;\n" +
            "    tail.next = null;\n" +
            "    head.next = tail;\n" +
            "    const sum = head.value + head.next.?.value;\n" +
            "    a.destroy(tail);\n" +
            "    a.destroy(head);\n" +
            "    return sum;\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n", 42, "" },
        // create_param (Milestone U) — create/destroy on an OPAQUE `std.mem.Allocator` parameter
        // dispatch INDIRECTLY through the vtable; the default materializes through the same path.
        new object[] { "create_param",
            "const std = @import(\"std\");\n" +
            "fn make(a: std.mem.Allocator, v: u8) !u8 {\n" +
            "    const p = try a.create(u8);\n" +
            "    p.* = v;\n" +
            "    const r = p.*;\n" +
            "    a.destroy(p);\n" +
            "    return r;\n" +
            "}\n" +
            "pub fn main() u8 { return make(std.heap.page_allocator, 42) catch 1; }\n", 42, "" },
        // arena (Milestone U) — an ArenaAllocator over the default; two allocations bump the same
        // chunk, `defer arena.deinit()` frees the chain at scope exit. The values are observable.
        new object[] { "arena",
            "const std = @import(\"std\");\n" +
            "fn run() !u8 {\n" +
            "    var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);\n" +
            "    defer arena.deinit();\n" +
            "    const a = arena.allocator();\n" +
            "    const s1 = try a.alloc(u8, 3);\n" +
            "    s1[0] = 10;\n" +
            "    s1[1] = 11;\n" +
            "    s1[2] = 9;\n" +
            "    const s2 = try a.alloc(u8, 2);\n" +
            "    s2[0] = 7;\n" +
            "    s2[1] = 5;\n" +
            "    const total = s1[0] + s1[1] + s1[2] + s2[0] + s2[1];\n" +
            "    return total;\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n", 42, "" },
        // realloc_cheap (Milestone U) — grow a slice on the devirtualized C-heap default (a direct
        // Libc.realloc); contents up to the old length are preserved, the new tail is written.
        new object[] { "realloc_cheap",
            "const std = @import(\"std\");\n" +
            "fn run() !u8 {\n" +
            "    const a = std.heap.page_allocator;\n" +
            "    var s = try a.alloc(u8, 2);\n" +
            "    s[0] = 10;\n" +
            "    s[1] = 11;\n" +
            "    s = try a.realloc(s, 4);\n" +
            "    s[2] = 12;\n" +
            "    s[3] = 9;\n" +
            "    const total = s[0] + s[1] + s[2] + s[3];\n" +
            "    a.free(s);\n" +
            "    return total;\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n", 42, "" },
        // realloc_param (Milestone U) — realloc through an OPAQUE `std.mem.Allocator` parameter,
        // EMULATED via the 2-fn vtable (alloc + copy-preserved-prefix + free).
        new object[] { "realloc_param",
            "const std = @import(\"std\");\n" +
            "fn grow(a: std.mem.Allocator) !u8 {\n" +
            "    var s = try a.alloc(u8, 2);\n" +
            "    s[0] = 30;\n" +
            "    s[1] = 0;\n" +
            "    s = try a.realloc(s, 4);\n" +
            "    s[2] = 12;\n" +
            "    s[3] = 0;\n" +
            "    const r = s[0] + s[2];\n" +
            "    a.free(s);\n" +
            "    return r;\n" +
            "}\n" +
            "pub fn main() u8 { return grow(std.heap.page_allocator) catch 1; }\n", 42, "" },
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
        // DEFER / ERRDEFER (Milestone H). Both observables (stdout AND exit) prove the lowering.
        // defer_lifo — two `defer`s run in reverse declaration order at block exit: the body prints
        // 'c' (99), then on exit 'b' (98) then 'a' (97) → "cba". (codes, since char literals are
        // not lexed yet.)
        new object[] { "defer_lifo",
            "extern fn putchar(c: c_int) c_int;\n" +
            "fn run() void { defer _ = putchar(97); defer _ = putchar(98); _ = putchar(99); }\n" +
            "pub fn main() u8 { run(); return 10; }\n", 10, "cba" },
        // defer_scope — an inner-block `defer` fires at the INNER block's exit, and the outer
        // `defer` fires even on an early `return`: '1'(49), 'i'(105 inner exit), '2'(50),
        // 'z'(122 outer defer on the early return) → "1i2z".
        new object[] { "defer_scope",
            "extern fn putchar(c: c_int) c_int;\n" +
            "fn run() u8 {\n" +
            "    defer _ = putchar(122);\n" +
            "    { defer _ = putchar(105); _ = putchar(49); }\n" +
            "    _ = putchar(50);\n" +
            "    if (1 == 1) return 10;\n" +
            "    return 99;\n" +
            "}\n" +
            "pub fn main() u8 { return run(); }\n", 10, "1i2z" },
        // errdefer_return — `errdefer` fires on an explicit `return error.X` (here mayFail(true)
        // prints 'E'=69), but NOT on the success return. a=3 (catch), b=7 → 10; stdout "E".
        new object[] { "errdefer_return",
            "extern fn putchar(c: c_int) c_int;\n" +
            "fn mayFail(fail: bool) !u8 { errdefer _ = putchar(69); if (fail) return error.Boom; return 7; }\n" +
            "fn run() u8 { const a = mayFail(1 == 1) catch 3; const b = mayFail(1 == 0) catch 3; return a + b; }\n" +
            "pub fn main() u8 { return run(); }\n", 10, "E" },
        // errdefer_propagate — `defer` + `errdefer` interleave LIFO, and `errdefer` fires on a
        // `try`-propagated error too. outer(true): 'R'(82 errdefer) then 'D'(68 defer), catch→9;
        // outer(false): 'D' only, →5. "RD" + "|"(124) + "D" = "RD|D"; 9 + 5 = 14.
        new object[] { "errdefer_propagate",
            "extern fn putchar(c: c_int) c_int;\n" +
            "fn inner(fail: bool) !u8 { if (fail) return error.X; return 1; }\n" +
            "fn outer(fail: bool) !u8 { defer _ = putchar(68); errdefer _ = putchar(82); const v = try inner(fail); return v + 4; }\n" +
            "fn run() u8 { const a = outer(1 == 1) catch 9; _ = putchar(124); const b = outer(1 == 0) catch 9; return a + b; }\n" +
            "pub fn main() u8 { return run(); }\n", 14, "RD|D" },
        // BOOL LITERALS. `true`/`false` as a typed decl, an inferred decl, an `if` condition, and a
        // `bool` argument. pick(true)=10 + pick(false)=20 + (a true → n=1) = 31.
        new object[] { "bool_literals",
            "fn pick(c: bool) u8 { if (c) return 10; return 20; }\n" +
            "pub fn main() u8 {\n" +
            "    const a: bool = true;\n" +
            "    const b = false;\n" +
            "    var n: u8 = 0;\n" +
            "    if (a) n = n + 1;\n" +
            "    if (b) n = n + 100;\n" +
            "    return pick(a) + pick(b) + n;\n" +
            "}\n", 31, "" },
        // CHAR LITERALS. Codepoints ('H'=72, 'i'=105, '\n'=10, '*'=42) and the escapes \t=9, \\=92,
        // \'=39, \x2A=42 all decode; prints "Hi" and returns 42 once the escapes check out.
        new object[] { "char_literals",
            "extern fn putchar(c: c_int) c_int;\n" +
            "pub fn main() u8 {\n" +
            "    _ = putchar('H');\n" +
            "    _ = putchar('i');\n" +
            "    _ = putchar('\\n');\n" +
            "    const star: u8 = '*';\n" +
            "    const tab: u8 = '\\t';\n" +
            "    const bs: u8 = '\\\\';\n" +
            "    const q: u8 = '\\'';\n" +
            "    const hx: u8 = '\\x2A';\n" +
            "    if (tab == 9 and bs == 92 and q == 39 and hx == 42) return star;\n" +
            "    return 0;\n" +
            "}\n", 42, "Hi" },
        // COMPOUND ASSIGNMENT. All 10 operators applied in a chain on one u8 lvalue, each doing
        // real work and landing on 42: 5 <<4=80 >>1=40 +10=50 -2=48 /2=24 *7=168 %50=18 |32=50
        // &46=34 ^8=42. Proves dotcc's `op=` lowering matches Zig's operator-by-operator.
        new object[] { "compound_assign",
            "pub fn main() u8 {\n" +
            "    var a: u8 = 5;\n" +
            "    a <<= 4; a >>= 1; a += 10; a -= 2; a /= 2;\n" +
            "    a *= 7; a %= 50; a |= 32; a &= 46; a ^= 8;\n" +
            "    return a;\n" +
            "}\n", 42, "" },
        // SINGLE-EVALUATION BINDING (the trap a `x = x op y` desugar would fail). The index
        // `bump(&calls)` has a side effect (it bumps `calls` via a pointer-deref compound assign
        // `p.* += 1`). `arr[bump(&calls)] += 2` must evaluate the lvalue ONCE: calls==1 and
        // arr[0]==42. A double-eval would call bump twice (calls==2) and return 0.
        new object[] { "compound_assign_eval_once",
            "fn bump(p: *u8) usize { p.* += 1; return 0; }\n" +
            "pub fn main() u8 {\n" +
            "    var calls: u8 = 0;\n" +
            "    var arr: [1]u8 = undefined;\n" +
            "    arr[0] = 40;\n" +
            "    arr[bump(&calls)] += 2;\n" +
            "    if (calls == 1) return arr[0];\n" +
            "    return 0;\n" +
            "}\n", 42, "" },
        // TOP-LEVEL GLOBALS. A typed `const`, an untyped `const` (comptime_int → int), and a
        // mutable `var` global bumped twice by a function (proving the mutation persists across
        // calls and resolves by bare name). 30 + 10 + 2 = 42.
        new object[] { "global_const_var",
            "const BONUS: u8 = 10;\n" +
            "const BASE = 30;\n" +
            "var counter: u8 = 0;\n" +
            "fn bump() void { counter += 1; }\n" +
            "pub fn main() u8 { bump(); bump(); return BASE + BONUS + counter; }\n", 42, "" },
        // A global's initializer references an EARLIER global by bare name (source-ordered lowering
        // → C# declaration-ordered field init). 20 + 22 = 42.
        new object[] { "global_const_ref",
            "const A: u8 = 20;\n" +
            "const B: u8 = A + 22;\n" +
            "pub fn main() u8 { return B; }\n", 42, "" },

        // --- Milestone I: lexer & literal completeness ---
        // Radix prefixes (0x/0o/0b) + `_` digit separators decode to the same value. 20+18+4 = 42.
        new object[] { "lexer_radix",
            "pub fn main() u8 {\n" +
            "    const a: u8 = 0x1_4;\n" +     // 20
            "    const b: u8 = 0o22;\n" +      // 18
            "    const c: u8 = 0b0_100;\n" +   // 4
            "    return a + b + c;\n" +
            "}\n", 42, "" },
        // A hex float `0x1.8p3` (= 12.0, no C# syntax → decimal) and an underscored decimal float.
        new object[] { "lexer_floats",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 {\n" +
            "    const hf: f64 = 0x1.8p3;\n" + // 12.0
            "    const df: f64 = 1_5.0;\n" +   // 15.0
            "    _ = printf(\"%.1f %.1f\\n\", hf, df);\n" +
            "    return 42;\n" +
            "}\n", 42, "12.0 15.0" },
        // Escaped quote `\"`, a `\u{41}` unicode escape ('A'), and a `\\`-prefixed multiline string
        // (lines joined by `\n`, no trailing newline). stdout: q="x" u=A / a / b.
        new object[] { "lexer_strings",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 {\n" +
            "    _ = printf(\"q=\\\"x\\\" u=\\u{41}\\n\");\n" +
            "    _ = printf(\n" +
            "        \\\\a\n" +
            "        \\\\b\n" +
            "    );\n" +
            "    _ = printf(\"\\n\");\n" +
            "    return 42;\n" +
            "}\n", 42, "q=\"x\" u=A\na\nb" },

        // --- Milestone J: result-location cast builtins (exit-code only — they prove the cast
        // SEMANTICS against real zig precisely, without the variadic-printf typing distraction) ---
        // @intCast narrows a wide usize to u8 — the result type comes from the binding, not an arg.
        new object[] { "builtin_intcast",
            "pub fn main() u8 {\n" +
            "    const wide: usize = 42;\n" +
            "    const narrow: u8 = @intCast(wide);\n" +
            "    return narrow;\n" +
            "}\n", 42, "" },
        // @truncate keeps the low byte of a u32 (0xFF2A & 0xFF = 0x2A = 42).
        new object[] { "builtin_truncate",
            "pub fn main() u8 {\n" +
            "    const big: u32 = 0xFF2A;\n" +
            "    const low: u8 = @truncate(big);\n" +
            "    return low;\n" +
            "}\n", 42, "" },
        // @bitCast reinterprets 1.0f's bits (0x3F800000); its biased exponent (bits >> 23) is 127.
        new object[] { "builtin_bitcast",
            "pub fn main() u8 {\n" +
            "    const bits: u32 = @bitCast(@as(f32, 1.0));\n" +
            "    const exp: u8 = @truncate(bits >> 23);\n" +
            "    return exp - 85;\n" +   // 127 - 85 = 42
            "}\n", 42, "" },
        // int -> f64 -> f32 -> int round trip via @floatFromInt / @floatCast / @intFromFloat.
        new object[] { "builtin_floatcast",
            "pub fn main() u8 {\n" +
            "    const i: i32 = 42;\n" +
            "    const f: f64 = @floatFromInt(i);\n" +
            "    const g: f32 = @floatCast(f);\n" +
            "    const back: u8 = @intFromFloat(g);\n" +
            "    return back;\n" +
            "}\n", 42, "" },
        // @enumFromInt(3) -> E.d, @intFromEnum -> 3, * 14 = 42.
        new object[] { "builtin_enumfromint",
            "const E = enum(u8) { a, b, c, d };\n" +
            "pub fn main() u8 {\n" +
            "    const e: E = @enumFromInt(3);\n" +
            "    const back: u8 = @intFromEnum(e);\n" +
            "    return back * 14;\n" +
            "}\n", 42, "" },
        // @ptrCast reinterprets a u32's storage as *u8 then back to *u32 (the up-cast needs
        // @alignCast); the low byte of 42 on a little-endian target is 42.
        new object[] { "builtin_ptrcast",
            "pub fn main() u8 {\n" +
            "    var x: u32 = 0;\n" +
            "    x = 42;\n" +
            "    const p8: *u8 = @ptrCast(&x);\n" +
            "    const p32: *u32 = @ptrCast(@alignCast(p8));\n" +
            "    return @intCast(p32.* & 0xFF);\n" +
            "}\n", 42, "" },
        // @sizeOf(u32) (= 4) folds into a constant; 4 * 10 + 2 = 42, narrowed by @intCast.
        new object[] { "builtin_sizeof",
            "pub fn main() u8 {\n" +
            "    const sz: usize = @sizeOf(u32);\n" +
            "    const n: u8 = @intCast(sz * 10 + 2);\n" +
            "    return n;\n" +
            "}\n", 42, "" },

        // --- Milestone K: array literals & aggregate globals (exit-code only) ---
        // Local array literals: anon `.{…}` at a [N]T sink, typed `[N]T{…}`, inferred `[_]T{…}`.
        // (33) + (6) + (9) - 6 = 42.
        new object[] { "array_literal_local",
            "pub fn main() u8 {\n" +
            "    const a: [3]u8 = .{ 10, 11, 12 };\n" +
            "    const b = [3]u8{ 1, 2, 3 };\n" +
            "    const c = [_]u8{ 4, 5 };\n" +
            "    return a[0] + a[1] + a[2] + b[0] + b[1] + b[2] + c[0] + c[1] - 6;\n" +
            "}\n", 42, "" },
        // Aggregate globals: a literal array global, an inferred-length array global, an
        // `undefined` array global (mutated in main), and a struct global. 33 + 3 + 6 + 0 = 42.
        new object[] { "array_global",
            "const Point = struct { x: u8, y: u8 };\n" +
            "const table: [3]u8 = .{ 10, 11, 12 };\n" +
            "const more = [_]u8{ 1, 2 };\n" +
            "var scratch: [2]u8 = undefined;\n" +
            "const origin: Point = .{ .x = 0, .y = 0 };\n" +
            "pub fn main() u8 {\n" +
            "    scratch[0] = 4;\n" +
            "    scratch[1] = 2;\n" +
            "    return table[0] + table[1] + table[2] + more[0] + more[1]\n" +
            "         + scratch[0] + scratch[1] + origin.x + origin.y;\n" +
            "}\n", 42, "" },

        // --- Milestone L (part 1): switch as an expression (exit-code only) ---
        // An int switch expression: a multi-value prong (`0, 1 => 5`) + an `else` default, at a
        // typed decl sink. n=2 → a=20, b=22 → 42.
        new object[] { "switch_expr_int",
            "pub fn main() u8 {\n" +
            "    const n: u8 = 2;\n" +
            "    const a: u8 = switch (n) { 0, 1 => 5, 2 => 20, else => 0 };\n" +
            "    const b: u8 = switch (n) { 0 => 1, else => 22 };\n" +
            "    return a + b;\n" +
            "}\n", 42, "" },
        // An enum switch expression in return position (`.member` labels + `else`). 20+12+10 = 42.
        new object[] { "switch_expr_enum",
            "const Color = enum(u8) { red, green, blue };\n" +
            "fn rank(c: Color) u8 { return switch (c) { .red => 10, .green => 20, else => 12 }; }\n" +
            "pub fn main() u8 { return rank(.green) + rank(.blue) + rank(.red); }\n", 42, "" },

        // --- Milestone L (part 2): labeled block as a value (exit-code only) ---
        // A typed-decl labeled value-block with an intermediate local. 10*2 + 22 = 42.
        new object[] { "labeled_block_decl",
            "pub fn main() u8 {\n" +
            "    const doubled: i32 = blk: {\n" +
            "        const half: i32 = 10;\n" +
            "        break :blk half * 2;\n" +
            "    };\n" +
            "    return @as(u8, @intCast(doubled + 22));\n" +
            "}\n", 42, "" },
        // A return-position labeled value-block with an early `break :blk` from inside an `if`
        // (the conditional break must stay conditional). classify(5)=10, classify(-1)=100 → 10+32 = 42.
        new object[] { "labeled_block_return",
            "fn classify(n: i32) i32 {\n" +
            "    return blk: {\n" +
            "        if (n < 0) break :blk 100;\n" +
            "        break :blk n * 2;\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var acc: i32 = 0;\n" +
            "    acc = blk: { const t = classify(-1); break :blk t - 68; };\n" + // 32
            "    return @as(u8, @intCast(classify(5) + acc));\n" +              // 10 + 32
            "}\n", 42, "" },

        // --- Milestone L (part 3): labeled loops + labeled break/continue (exit-code only) ---
        // `break :outer` from a nested loop finds the first (i,j) with i*j>=6 → (2,3); plus a
        // `continue :scan` that counts 2 inner iterations per outer (skipping a trailing add).
        // 2*3 (found) = 6, scan count = 6 → 6*6 + 6 = 42.
        new object[] { "labeled_loop_break_continue",
            "pub fn main() u8 {\n" +
            "    var fi: i32 = 0; var fj: i32 = 0; var i: i32 = 1;\n" +
            "    outer: while (i <= 5) : (i = i + 1) {\n" +
            "        var j: i32 = 1;\n" +
            "        while (j <= 5) : (j = j + 1) {\n" +
            "            if (i * j >= 6) { fi = i; fj = j; break :outer; }\n" +
            "        }\n" +
            "    }\n" +
            "    var count: i32 = 0; var hits: i32 = 0; var a: i32 = 0;\n" +
            "    scan: while (a < 3) : (a = a + 1) {\n" +
            "        var b: i32 = 0;\n" +
            "        while (b < 3) : (b = b + 1) {\n" +
            "            count = count + 1;\n" +
            "            if (b == 1) continue :scan;\n" +
            "        }\n" +
            "        hits = hits + 100;\n" +
            "    }\n" +
            "    return @as(u8, @intCast(count * count + fi * fj + hits));\n" + // 36 + 6 + 0
            "}\n", 42, "" },
        // A labeled `for` (range) loop with `continue :row` from a nested for. 3 rows × 2 counted
        // inner iters = 6; 6*7 = 42.
        new object[] { "labeled_for_continue",
            "pub fn main() u8 {\n" +
            "    var n: i32 = 0;\n" +
            "    row: for (0..3) |_| {\n" + // index unused → `|_|` (real zig errors on an unused capture)
            "        for (0..3) |c| {\n" +
            "            n = n + 1;\n" +
            "            if (c == @as(usize, 1)) continue :row;\n" +
            "        }\n" +
            "        n = n + 100;\n" +
            "    }\n" +
            "    return @as(u8, @intCast(n * 7));\n" + // 6 * 7
            "}\n", 42, "" },

        // --- Milestone L (part 4): switch ranges (exit-code only) ---
        // A switch EXPRESSION with `lo...hi` ranges (a char classifier) + a STATEMENT switch with
        // ranges and a multi-value prong. bucket(2)*18 + kinds(1+2+3+0=6) = 42.
        new object[] { "switch_range",
            "fn kind(c: u8) u8 {\n" +
            "    return switch (c) {\n" +
            "        '0'...'9' => 1,\n" +
            "        'A'...'Z' => 2,\n" +
            "        'a'...'z' => 3,\n" +
            "        else => 0,\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var bucket: i32 = 0;\n" +
            "    const n: i32 = 42;\n" +
            "    switch (n) {\n" +
            "        0...9 => { bucket = 1; },\n" +
            "        10...99 => { bucket = 2; },\n" +
            "        100, 200, 300 => { bucket = 3; },\n" +
            "        else => { bucket = 9; },\n" +
            "    }\n" +
            "    const s: i32 = kind('7') + kind('Q') + kind('z') + kind('!');\n" + // 1+2+3+0 = 6
            "    return @as(u8, @intCast(bucket * 18 + s));\n" +                    // 36 + 6
            "}\n", 42, "" },

        // --- Milestone M (part 1): optional payload capture in `if` (exit-code only) ---
        // Value optional then/else/`_`/no-else + a niche optional-pointer capture written through.
        // 4 (then) + 10 (else) + 8 (discard) + 20 (ptr write) = 42.
        new object[] { "if_capture",
            "fn pick(p: bool, v: i32) ?i32 { if (p) return v; return null; }\n" +
            "pub fn main() u8 {\n" +
            "    var sum: i32 = 0;\n" +
            "    if (pick(true, 4)) |e| { sum += e; } else { sum += 100; }\n" +
            "    if (pick(false, 9)) |e| { sum += e; } else { sum += 10; }\n" +
            "    if (pick(true, 8)) |_| { sum += 8; }\n" +
            "    if (pick(false, 5)) |e| { sum += e; }\n" +
            "    var k: i32 = 0;\n" +
            "    const maybe: ?*i32 = &k;\n" +
            "    if (maybe) |p| { p.* = 20; }\n" +
            "    sum += k;\n" +
            "    return @as(u8, @intCast(sum));\n" + // 4 + 10 + 8 + 20
            "}\n", 42, "" },

        // --- Milestone M (part 2): optional capture-`while` (exit-code only) ---
        // A value-optional capture-while summing a `nextLT` iterator 0..8 = 36, plus a `_` discard
        // capture-while counting 6 iterations = +6. 36 + 6 = 42.
        new object[] { "while_capture",
            "fn nextLT(i: *i32, max: i32) ?i32 { if (i.* >= max) return null; const v = i.*; i.* += 1; return v; }\n" +
            "pub fn main() u8 {\n" +
            "    var sum: i32 = 0;\n" +
            "    var i: i32 = 0;\n" +
            "    while (nextLT(&i, 9)) |v| { sum += v; }\n" + // 0+1+…+8 = 36
            "    var j: i32 = 0;\n" +
            "    var count: i32 = 0;\n" +
            "    while (nextLT(&j, 6)) |_| { count += 1; }\n" + // 6 iterations
            "    sum += count;\n" +
            "    return @as(u8, @intCast(sum));\n" + // 36 + 6
            "}\n", 42, "" },

        // --- Milestone M (part 3): error-union capture in `if` (exit-code only) ---
        // Payload capture `|x|` on success + the error branch on failure, both via `else |_|` (the
        // both-compiler-valid subset: real zig REJECTS a plain `else` on an error union and rejects
        // `_ = e;`, so the error is discarded with `|_|`; a USED named `|e|` awaits the error-set
        // milestone). success(+20) + failure-else(+22) = 42.
        new object[] { "error_capture",
            "fn tryVal(ok: bool, v: i32) !i32 { if (ok) return v; return error.Bad; }\n" +
            "pub fn main() u8 {\n" +
            "    var sum: i32 = 0;\n" +
            "    if (tryVal(true, 20)) |x| { sum += x; } else |_| { sum += 100; }\n" + // success → +20
            "    if (tryVal(false, 99)) |x| { sum += x; } else |_| { sum += 22; }\n" + // failure → +22
            "    return @as(u8, @intCast(sum));\n" + // 20 + 22
            "}\n", 42, "" },

        // --- Milestone M (part 4): by-reference capture `|*x|` (exit-code only) ---
        // `for (s) |*e|` doubles a slice in place (3,4,5,6 → 6,8,10,12 = 36); `switch (b) |*p|`
        // mutates the union payload in place (+6, read back via a by-value capture). 36 + 6 = 42.
        new object[] { "byref_capture",
            "const Box = union(enum) { i: i32, f: i32 };\n" +
            "pub fn main() u8 {\n" +
            "    var sum: i32 = 0;\n" +
            "    var arr = [_]i32{ 3, 4, 5, 6 };\n" +
            "    const s: []i32 = arr[0..4];\n" +
            "    for (s) |*e| { e.* = e.* * 2; }\n" + // double in place
            "    for (s) |e| { sum += e; }\n" + // 6+8+10+12 = 36
            "    var b: Box = .{ .i = 0 };\n" +
            "    switch (b) { .i => |*p| { p.* = 6; }, .f => |*p| { p.* = 0; } }\n" + // mutate payload
            "    switch (b) { .i => |v| { sum += v; }, .f => |v| { sum += v; } }\n" + // +6
            "    return @as(u8, @intCast(sum));\n" + // 36 + 6
            "}\n", 42, "" },

        // --- Milestone N (part 1): error values — bare `error.X` + `==`/`!=` (exit-code only) ---
        // A USED captured error compared against a named error (the part-3 payoff, now both-valid):
        // failure → `e == error.Bad` matches → +20. A bare `error.X` const compared two ways: `==`
        // matches (+10), `!=` against a different error matches (+12). 20 + 10 + 12 = 42.
        new object[] { "error_value",
            "fn tryVal(ok: bool, v: i32) !i32 { if (ok) return v; return error.Bad; }\n" +
            "pub fn main() u8 {\n" +
            "    var sum: i32 = 0;\n" +
            "    if (tryVal(false, 99)) |x| { sum += x; } else |e| { if (e == error.Bad) { sum += 20; } else { sum += 100; } }\n" +
            "    const want = error.Bad;\n" +
            "    if (want == error.Bad) { sum += 10; }\n" + // matched
            "    if (want == error.Other) { sum += 100; }\n" + // not matched
            "    if (want != error.Other) { sum += 12; }\n" + // matched (inequality)
            "    return @as(u8, @intCast(sum));\n" + // 20 + 10 + 12
            "}\n", 42, "" },

        // --- Milestone N (part 2): `switch (e)` on an error value (exit-code only) ---
        // An error value IS its flat code, so an error switch lowers to an integer switch (each
        // `error.X` prong → a `case <code>:`, `else` → `default:`). The error is captured from
        // `else |e|`. score(0)→error.Zero→+20, score(-3)→error.Negative→+5, score(17)→ok→+17 = 42.
        new object[] { "error_switch",
            "fn classify(n: i32) anyerror!i32 { if (n == 0) return error.Zero; if (n < 0) return error.Negative; return n; }\n" +
            "fn score(n: i32, sum: *i32) void {\n" +
            "    if (classify(n)) |v| { sum.* += v; } else |e| {\n" +
            "        switch (e) { error.Zero => { sum.* += 20; }, error.Negative => { sum.* += 5; }, else => { sum.* += 1; } }\n" +
            "    }\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var sum: i32 = 0;\n" +
            "    score(0, &sum); score(-3, &sum); score(17, &sum);\n" + // +20 +5 +17
            "    return @as(u8, @intCast(sum));\n" +
            "}\n", 42, "" },

        // --- Milestone N (part 3): `catch |e|` capture + lazy/side-effecting `catch` (exit-code only) ---
        // `catch |e| (e == error.Bad)` binds the error and uses it (a bool fallback): success→true,
        // error→`Bad==Bad`=true → +10 +12. A side-effecting (call) fallback `catch dflt()` runs the
        // call only on error: mk(true)→7, mk(false)→dflt()=13 → +7 +13. 10+12+7+13 = 42.
        new object[] { "error_catch",
            "fn mayBool(ok: bool) !bool { if (ok) return true; return error.Bad; }\n" +
            "fn mk(ok: bool) !i32 { if (ok) return 7; return error.Bad; }\n" +
            "fn dflt() i32 { return 13; }\n" +
            "pub fn main() u8 {\n" +
            "    var sum: i32 = 0;\n" +
            "    const a = mayBool(true) catch |e| (e == error.Bad);\n" +
            "    const b = mayBool(false) catch |e| (e == error.Bad);\n" +
            "    if (a) sum += 10;\n" +
            "    if (b) sum += 12;\n" +
            "    const c = mk(true) catch dflt();\n" +
            "    const d = mk(false) catch dflt();\n" +
            "    sum += c; sum += d;\n" + // +7 +13
            "    return @as(u8, @intCast(sum));\n" +
            "}\n", 42, "" },

        // --- Milestone N (part 4): error-union `main` (`!void` / `!u8`) ---
        // `pub fn main() !void` — success path (the error is not taken): exit 0, prints "ok".
        new object[] { "main_errunion_void",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "fn step(go: bool) !void { if (go) return error.Bad; }\n" +
            "pub fn main() !void {\n" +
            "    try step(false);\n" +
            "    _ = printf(\"ok\\n\");\n" +
            "}\n", 0, "ok" },
        // `pub fn main() !u8` — success: the payload IS the exit code (42).
        new object[] { "main_errunion_u8",
            "fn mk(ok: bool) !u8 { if (ok) return 42; return error.Bad; }\n" +
            "pub fn main() !u8 { const v = try mk(true); return v; }\n", 42, "" },
        // `pub fn main() !u8` — error path: the error propagates out of main → exit 1 (the error is
        // reported to stderr in both compilers, so stdout stays empty).
        new object[] { "main_errunion_err",
            "fn mk(ok: bool) !u8 { if (ok) return 42; return error.Bad; }\n" +
            "pub fn main() !u8 { const v = try mk(false); return v; }\n", 1, "" },

        // --- Milestone N (part 5): explicit `error{A, B}` set declarations + named `E!T` ---
        // A named error set used as an `E!T` return type; dotcc erases the set (`E` emits nothing).
        // checked(5)→10, checked(-1)→error.Negative→catch 12, checked(200)→error.Overflow→catch 20.
        new object[] { "error_set",
            "const MathError = error{ Overflow, Negative };\n" +
            "fn checked(n: i32) MathError!i32 { if (n > 100) return error.Overflow; if (n < 0) return error.Negative; return n * 2; }\n" +
            "pub fn main() u8 {\n" +
            "    var sum: i32 = 0;\n" +
            "    sum += checked(5) catch 0;\n" + // +10
            "    sum += checked(-1) catch 12;\n" + // +12
            "    sum += checked(200) catch 20;\n" + // +20
            "    return @as(u8, @intCast(sum));\n" + // 42
            "}\n", 42, "" },

        // --- Milestone N (part 6): control-flow `catch return` / `orelse return` ---
        // decl `catch return` (error union) + decl `orelse return` (value optional). compute(t,t)→30;
        // compute(f,t)→mk errors→`catch return error.NoX`→compute Err→main `catch 12`. 30+12 = 42.
        new object[] { "cf_return",
            "fn mk(ok: bool) !i32 { if (ok) return 10; return error.Bad; }\n" +
            "fn pick(b: bool) ?i32 { if (b) return 20; return null; }\n" +
            "fn compute(a: bool, b: bool) !i32 {\n" +
            "    const x = mk(a) catch return error.NoX;\n" +
            "    const y = pick(b) orelse return 0;\n" +
            "    return x + y;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var sum: i32 = 0;\n" +
            "    sum += compute(true, true) catch 99;\n" + // 30
            "    sum += compute(false, true) catch 12;\n" + // 12
            "    return @as(u8, @intCast(sum));\n" + // 42
            "}\n", 42, "" },
        // statement-position `catch return` (a `!void` early-out) + a niche-pointer `orelse return`.
        // run(false)→7; run(true)→error.Stop→catch 30; deref(&k)→0; deref(null)→orelse 5. 7+30+0+5 = 42.
        new object[] { "cf_return_stmt",
            "fn step(go: bool) !void { if (go) return error.Bad; }\n" +
            "fn run(go: bool) !u8 { step(go) catch return error.Stop; return 7; }\n" +
            "fn deref(p: ?*i32) i32 { const q = p orelse return 5; return q.*; }\n" +
            "pub fn main() u8 {\n" +
            "    var sum: i32 = 0;\n" +
            "    sum += run(false) catch 0;\n" + // 7
            "    sum += run(true) catch 30;\n" + // 30
            "    var k: i32 = 0;\n" +
            "    sum += deref(&k);\n" + // 0
            "    sum += deref(null);\n" + // 5
            "    return @as(u8, @intCast(sum));\n" + // 42
            "}\n", 42, "" },
        // open-ended slicing `s[lo..]`: the high bound is the source length. Slice source via
        // `.len` (21+14+7 = 42); array source through the offset element pointer (t[0], t.len);
        // a closed re-slice still works. Element read proves `.ptr + lo`; lengths prove `len - lo`.
        new object[] { "open_slice",
            "pub fn main() u8 {\n" +
            "    const s: []const u8 = \"abcdefghijklmnopqrstu\";\n" + // len 21
            "    const a = s[0..].len + s[7..].len + s[14..].len;\n" + // 21+14+7 = 42
            "    const arr = [_]u8{ 5, 10, 20, 12 };\n" +
            "    const t = arr[1..];\n" + // {10,20,12}
            "    if (t[0] != 10) return 1;\n" + // element through the offset pointer
            "    if (t.len != 3) return 2;\n" + // array open-ended length
            "    if (s[2..5].len != 3) return 3;\n" + // closed re-slice still works
            "    return @as(u8, @intCast(a));\n" + // 42
            "}\n", 42, "" },
        // many-item pointers `[*]T`: index `p[i]`, closed-slice `p[0..3]` into a slice (+ .len),
        // and bind a slice's `.ptr` (a `[*]const u8`) across. `'*'` is ASCII 42, so first(p)=42.
        new object[] { "many_ptr",
            "fn first(p: [*]const u8) u8 { return p[0]; }\n" +
            "fn take3(p: [*]const u8) usize { const sl = p[0..3]; return sl.len; }\n" +
            "pub fn main() u8 {\n" +
            "    const s: []const u8 = \"*bcdef\";\n" + // s.ptr[0] = '*' = 42
            "    const p: [*]const u8 = s.ptr;\n" + // slice .ptr is a many-item pointer
            "    if (take3(p) != 3) return 1;\n" + // closed slice of a [*]T -> .len
            "    return first(p);\n" + // 42
            "}\n", 42, "" },
        // sentinel-terminated types: `[:0]const u8` slice (.len excludes the NUL), `[*:0]const u8`
        // C-string pointer (manual scan to the sentinel since string literals are NUL-terminated).
        // s.len(21) + clen(21) = 42; `s.ptr` is a `[*:0]const u8`.
        new object[] { "sentinel",
            "fn clen(p: [*:0]const u8) usize {\n" +
            "    var n: usize = 0;\n" +
            "    while (p[n] != 0) : (n = n + 1) {}\n" +
            "    return n;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const s: [:0]const u8 = \"abcdefghijklmnopqrstu\";\n" + // .len = 21
            "    const p: [*:0]const u8 = s.ptr;\n" +
            "    return @as(u8, @intCast(s.len + clen(p)));\n" + // 21 + 21 = 42
            "}\n", 42, "" },

        // `[N:0]T` sentinel arrays (Milestone O, part 4): N+1 storage, the trailing slot is the
        // sentinel 0 (so the buffer is a NUL-terminated C string), logical length N. The 5 elements
        // sum to 42; `buf[5]` reads back the reserved sentinel slot (must be 0).
        new object[] { "sentinel_array",
            "pub fn main() u8 {\n" +
            "    const buf: [5:0]u8 = .{ 10, 11, 12, 8, 1 };\n" + // 5 logical bytes -> 42
            "    var sum: u32 = 0;\n" +
            "    var i: usize = 0;\n" +
            "    while (i < 5) : (i = i + 1) { sum = sum + buf[i]; }\n" +
            "    if (buf[5] != 0) return 1;\n" + // the reserved sentinel slot is 0
            "    return @as(u8, @intCast(sum));\n" + // 42
            "}\n", 42, "" },

        // Non-escaping stack-slice peephole (Milestone O, part 5): a page_allocator (devirt'd
        // C-heap) byte slice that is constant-size, freed, and used only via s[i]/s.len is demoted
        // to a `stackalloc` backing on dotcc (the heap alloc/free vanish). Real zig heap-allocs +
        // frees; both observe 6 * 7 = 42.
        new object[] { "stack_slice",
            "const std = @import(\"std\");\n" +
            "fn run() !u8 {\n" +
            "    const a = std.heap.page_allocator;\n" +
            "    const buf = try a.alloc(u8, 6);\n" +
            "    var i: usize = 0;\n" +
            "    while (i < buf.len) : (i = i + 1) { buf[i] = 7; }\n" +
            "    var sum: u32 = 0;\n" +
            "    i = 0;\n" +
            "    while (i < buf.len) : (i = i + 1) { sum = sum + buf[i]; }\n" +
            "    a.free(buf);\n" +
            "    return @as(u8, @intCast(sum));\n" + // 6 * 7 = 42
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n", 42, "" },

        // Wrapping arithmetic (Milestone P, part 1): `+%`/`-%`/`*%` + the compound forms. Two's-
        // complement wrap at the operand width (Zig has no integer promotion); the `u32` slot proves
        // the wrap is at the `u8` operand width (260 -> 4), NOT the result location. Lands on 42.
        new object[] { "wrap_ops",
            "pub fn main() u8 {\n" +
            "    var x: u8 = 200;\n" +
            "    x +%= 100;\n" + // 300 -> 44
            "    x -%= 2;\n" +   // 42
            "    var k: u8 = 16;\n" +
            "    k *%= 16;\n" +  // 256 -> 0
            "    if (k != 0) return 1;\n" +
            "    const z: u8 = 0;\n" +
            "    const u: u8 = z -% 2;\n" + // 0 -% 2 -> 254
            "    if (u != 254) return 2;\n" +
            "    const a: u8 = 250;\n" +
            "    const b: u8 = 10;\n" +
            "    const w: u32 = a +% b;\n" + // 260 wraps at u8 -> 4, then widens -> 4
            "    if (w != 4) return 3;\n" +
            "    return x;\n" + // 42
            "}\n", 42, "" },

        // Saturating arithmetic (Milestone P, part 2): `+|`/`-|`/`*|` + compound. Clamp to the
        // operand-type range (signed both ends, unsigned floor at 0); the `u32` slot proves the
        // clamp is at the `u8` operand width (260 -> 255) before widening. Lands on 42.
        new object[] { "sat_ops",
            "pub fn main() u8 {\n" +
            "    var x: u8 = 200;\n" +
            "    x +|= 100;\n" + // 300 -> 255
            "    if (x != 255) return 1;\n" +
            "    var y: u8 = 5;\n" +
            "    y -|= 10;\n" +  // -> 0
            "    if (y != 0) return 2;\n" +
            "    var k: u8 = 100;\n" +
            "    k *|= 100;\n" + // 10000 -> 255
            "    if (k != 255) return 3;\n" +
            "    var s: i8 = 100;\n" +
            "    s +|= 100;\n" + // 200 -> 127
            "    if (s != 127) return 4;\n" +
            "    var n: i8 = -100;\n" +
            "    n -|= 100;\n" + // -200 -> -128
            "    if (n != -128) return 5;\n" +
            "    const a: u8 = 250;\n" +
            "    const b: u8 = 10;\n" +
            "    const w: u32 = a +| b;\n" + // 260 clamps at u8 -> 255, then widens -> 255
            "    if (w != 255) return 6;\n" +
            "    const base: u8 = 40;\n" +
            "    return base +| 2;\n" + // 42 (no saturation)
            "}\n", 42, "" },

        // Destructuring completeness (Milestone S): assign-to-existing lvalues, mixed new+existing,
        // typed binders, and the `_` discard. Zig destructuring is SEQUENTIAL — for a tuple-literal
        // RHS an existing-lvalue write is visible to a later element's read, so `a, b = .{ b, a }` is
        // NOT a swap (a<-old b, then b<-the new a). A non-literal tuple RHS (`pair()`) single-evals.
        new object[] { "destructure",
            "fn pair() struct { u8, u8 } { return .{ 20, 22 }; }\n" +
            "pub fn main() u8 {\n" +
            "    var a: u8 = 3;\n" +
            "    var b: u8 = 9;\n" +
            "    a, b = .{ b, a };\n" +          // a<-9, b<-new a (9): NOT a swap
            "    if (a != 9 or b != 9) return 1;\n" +
            "    var p: u8 = 1;\n" +
            "    var q: u8 = 2;\n" +
            "    var r: u8 = 3;\n" +
            "    p, q, r = .{ q, r, p };\n" +     // p<-2, q<-3, r<-new p (2)
            "    if (p != 2 or q != 3 or r != 2) return 2;\n" +
            "    var c: u8 = 0;\n" +
            "    const d, c = .{ 5, 6 };\n" +     // mixed: new const + existing lvalue
            "    if (d != 5 or c != 6) return 3;\n" +
            "    const e: u16, const f: u8 = .{ 300, 7 };\n" + // typed binders drive result-location
            "    if (e != 300 or f != 7) return 4;\n" +
            "    var g: u8 = 0;\n" +
            "    _, g = .{ 99, 8 };\n" +          // `_` discard
            "    if (g != 8) return 5;\n" +
            "    const x, const y = pair();\n" +  // non-literal RHS: single-eval into a temp
            "    if (x != 20 or y != 22) return 6;\n" +
            "    return c + f + x + g + 1;\n" +   // 6 + 7 + 20 + 8 + 1 = 42
            "}\n", 42, "" },

        // `union(SomeEnum)` (Milestone R): a tagged union whose discriminant is an EXISTING named
        // enum. `Kind` uses non-zero/out-of-order values (1/2/4), so the tag VALUE comes from the
        // named enum (not a synthesized 0-based one). Reuses the tagged-union construct + switch +
        // payload-capture lowering. Stdout proves @intFromEnum(Kind.flag)==4; lands on 42.
        new object[] { "union_tagged",
            "const Kind = enum(u8) { num = 1, small = 2, flag = 4 };\n" +
            "const Value = union(Kind) { num: i32, small: u8, flag: bool };\n" +
            "fn score(v: Value) u8 {\n" +
            "    switch (v) {\n" +
            "        .num => |x| { return @intCast(x); },\n" +
            "        .small => |y| { return y; },\n" +
            "        .flag => |z| { return if (z) 100 else 0; },\n" +
            "    }\n" +
            "}\n" +
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 {\n" +
            "    const a: Value = .{ .num = 30 };\n" +
            "    const b: Value = .{ .small = 12 };\n" +
            "    _ = printf(\"flagtag=%d\\n\", @as(c_int, @intFromEnum(Kind.flag)));\n" +
            "    return score(a) + score(b);\n" + // 30 + 12 = 42
            "}\n", 42, "flagtag=4" },             // expected stdout is newline-trimmed (see Norm)

        // Struct layout modifiers (Milestone R, part 2): `extern struct` (C-ABI sequential) vs
        // `packed struct` (byte-packed, no padding). @sizeOf(Ext{u8,u32}) = 8 (aligned + tail pad);
        // @sizeOf(Pk{4×u8}) = 4 (32 bits → matches Zig's bit-backing model for byte-multiple fields).
        // Field read/write on both. 3 + 11 + 1 + 2 + 3 + 10 + 12(sz) = 42; stdout proves sz = 12.
        new object[] { "struct_layout",
            "const Ext = extern struct { a: u8, b: u32 };\n" +
            "const Pk = packed struct { a: u8, b: u8, c: u8, d: u8 };\n" +
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 {\n" +
            "    var e: Ext = .{ .a = 3, .b = 7 };\n" +
            "    e.b += 4;\n" + // 11
            "    var p: Pk = .{ .a = 1, .b = 2, .c = 3, .d = 4 };\n" +
            "    p.d += 6;\n" + // 10
            "    const sz: u32 = @sizeOf(Ext) + @sizeOf(Pk);\n" + // 8 + 4 = 12
            "    const szc: c_int = @intCast(sz);\n" +
            "    _ = printf(\"sz=%d\\n\", szc);\n" +
            "    const total: u32 = @as(u32, e.a) + e.b + @as(u32, p.a) + @as(u32, p.b) + @as(u32, p.c) + @as(u32, p.d) + sz;\n" +
            "    return @intCast(total);\n" + // 42
            "}\n", 42, "sz=12" },

        // Untagged `union { … }` (Milestone R, part 3): no discriminant — a bare overlapping-storage
        // union. dotcc lowers it to a [StructLayout(Explicit)] overlay struct. Each value keeps to a
        // single ACTIVE field (write-then-read the same field): Zig's safe-mode active-field tracking
        // isn't modeled, so punning is out of scope. a.small(15) + b.big(27) = 42; stdout proves b.big.
        new object[] { "union_untagged",
            "const Box = union { small: u8, big: u32 };\n" +
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 {\n" +
            "    var a: Box = .{ .small = 10 };\n" +
            "    a.small += 5;\n" + // 15
            "    var b: Box = .{ .big = 25 };\n" +
            "    b.big += 2;\n" + // 27
            "    const bc: c_int = @intCast(b.big);\n" +
            "    _ = printf(\"u=%d\\n\", bc);\n" +
            "    return @intCast(@as(u32, a.small) + b.big);\n" + // 42
            "}\n", 42, "u=27" },

        // FFI declaration surface (Milestone R, part 4): `extern "c" fn` (library/calling-convention
        // string) + `export fn` / `pub export fn` (C-ABI external linkage). dotcc lowers extern "c"
        // like a plain extern fn (routed to its libc runtime) and emits export functions as ordinary
        // callable ones. mul(20,2)=40, add(40,2)=42; stdout proves the extern "c" printf.
        new object[] { "export_extern",
            "extern \"c\" fn printf(format: [*c]const u8, ...) c_int;\n" +
            "export fn add(a: u8, b: u8) u8 { return a + b; }\n" +
            "pub export fn mul(a: u8, b: u8) u8 { return a * b; }\n" +
            "pub fn main() u8 {\n" +
            "    const r = add(mul(20, 2), 2);\n" + // 42
            "    _ = printf(\"r=%d\\n\", @as(c_int, r));\n" +
            "    return r;\n" +
            "}\n", 42, "r=42" },

        // Exported / public DATA globals: `export const`/`export var`, `pub const`/`pub var`, and
        // `pub export const` — the modifier is peeled (Unwrap) and each lowers as an ordinary global.
        new object[] { "export_data",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "export const answer: i32 = 42;\n" +
            "pub const greeting: i32 = 7;\n" +
            "pub var counter: i32 = 0;\n" +
            "export var total: i32 = 0;\n" +
            "pub export const shared: i32 = 3;\n" +
            "pub fn main() u8 {\n" +
            "    counter = answer;\n" +
            "    total = greeting + shared;\n" + // 7 + 3 = 10
            "    _ = printf(\"counter=%d total=%d\\n\", counter, total);\n" +
            "    return @intCast(total);\n" +
            "}\n", 10, "counter=42 total=10" },

        // Declaration modifiers (Milestone R, part 5): callconv / align / linksection — all no-ops on
        // the managed target, accepted for round-trippability. `linksection` on a global var, `callconv`
        // on a function, `align` on a local. tag(11)=12, buf=30+12=42; stdout proves the value.
        new object[] { "decl_modifiers",
            "extern \"c\" fn printf(format: [*c]const u8, ...) c_int;\n" +
            "var counter: u32 linksection(\".mydata\") = 0;\n" +
            "fn tag(x: u8) callconv(.c) u8 { return x + 1; }\n" +
            "pub fn main() u8 {\n" +
            "    var buf: u32 align(8) = 30;\n" +
            "    buf += tag(11);\n" + // 42
            "    counter = buf;\n" +
            "    const c: c_int = @intCast(counter);\n" +
            "    _ = printf(\"c=%d\\n\", c);\n" +
            "    return @intCast(counter);\n" + // 42
            "}\n", 42, "c=42" },

        // Container-level `var` (a namespaced mutable global) + sibling-const-by-bare-name (Milestone R,
        // part 6). `Cfg.counter` is a mutable global; `Cfg.doubled = base * 2` references the sibling
        // `const base` by bare name. 20 + 10 + 12 = 42; stdout proves the running counter.
        new object[] { "container_var",
            "extern \"c\" fn printf(format: [*c]const u8, ...) c_int;\n" +
            "const Cfg = struct {\n" +
            "    const base: u32 = 10;\n" +
            "    const doubled: u32 = base * 2;\n" + // bare `base` → 20
            "    var counter: u32 = 0;\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    Cfg.counter = Cfg.doubled;\n" + // 20
            "    Cfg.counter += Cfg.base;\n" + // 30
            "    Cfg.counter += 12;\n" + // 42
            "    const c: c_int = @intCast(Cfg.counter);\n" +
            "    _ = printf(\"counter=%d\\n\", c);\n" +
            "    return @intCast(Cfg.counter);\n" + // 42
            "}\n", 42, "counter=42" },

        // Milestone ß ("sharp-s"): 128-bit integers i128/u128 → C# System.Int128/UInt128 (BCL
        // primitives, arithmetic free). The values genuinely exceed 64 bits (2^80 via a u128
        // multiply; -(2^100) as a signed i128 with a sign-preserving arithmetic shift), so a 64-bit
        // lowering would truncate them. Reduced to a byte exit code; stdout proves the wide values.
        new object[] { "int128",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 {\n" +
            "    const a: u128 = @as(u128, 1) << 40;\n" +
            "    const wide: u128 = a * a;\n" +              // 2^80
            "    const hi: u64 = @intCast(wide >> 64);\n" +  // 2^16 = 65536
            "    const big: i128 = -(@as(i128, 1) << 100);\n" +
            "    const neg: i64 = @intCast(big >> 100);\n" + // -1
            "    const code: u64 = (hi / 65536) + @as(u64, @intCast(-neg)) + 40;\n" + // 1 + 1 + 40 = 42
            "    _ = printf(\"hi=%llu neg=%lld\\n\", hi, neg);\n" +
            "    return @intCast(code);\n" + // 42
            "}\n", 42, "hi=65536 neg=-1" },

        // Milestone T (part 1): the shared comptime interpreter folds constant expressions
        // that the old Zig-side folder rejected — a binary-op enum initializer (`1 << 2`,
        // `(1 << 3) - 1` → all=7) and a computed array size (`[2 * 8]u8` → 16 elements, so
        // index 15 is in bounds). Real zig 0.17 folds both; dotcc now agrees. 7 + 2 + 33 = 42.
        new object[] { "comptime-fold",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "const Flags = enum(u8) { read = 1 << 0, write = 1 << 1, exec = 1 << 2, all = (1 << 3) - 1 };\n" +
            "pub fn main() u8 {\n" +
            "    var buf: [2 * 8]u8 = undefined;\n" +              // computed size -> 16 elements
            "    buf[15] = @intFromEnum(Flags.all);\n" +          // last slot in bounds + folded enum -> 7
            "    buf[0] = @intFromEnum(Flags.write);\n" +         // 2
            "    _ = printf(\"all=%d write=%d\\n\", @as(c_int, buf[15]), @as(c_int, buf[0]));\n" +
            "    return buf[15] + buf[0] + 33;\n" +               // 7 + 2 + 33 = 42
            "}\n", 42, "all=7 write=2" },

        // Milestone T (part 2): `comptime EXPR` forces compile-time evaluation and splices the
        // result as a literal — arithmetic (`comptime (2 + 3) * 4` → 20), `@sizeOf` (8), and a
        // relational used as a comptime-known condition (`comptime (7 > 3)` → true). Real zig 0.17
        // computes the same; a=20, sz=8, r=28, exit 28.
        new object[] { "comptime-expr",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 {\n" +
            "    const a: u32 = comptime (2 + 3) * 4;\n" +        // 20
            "    const sz: u32 = comptime @sizeOf(u64);\n" +      // 8
            "    var r: u32 = a + sz;\n" +                        // 28
            "    if (comptime (7 > 3)) { r += 0; }\n" +           // comptime-known true branch
            "    _ = printf(\"a=%u sz=%u r=%u\\n\", a, sz, r);\n" +
            "    return @intCast(r);\n" +                         // 28
            "}\n", 28, "a=20 sz=8 r=28" },

        // Milestone T (part 2b): `comptime fib(10)` / `comptime fact(5)` interpret the callee at
        // compile time — recursion + a call frame for fib, a while-loop + local mutation for fact —
        // and splice the results (55, 120) as literals. Real zig 0.17 computes the same; 55+120-133=42.
        new object[] { "comptime-call",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "fn fib(n: u32) u32 { if (n < 2) return n; return fib(n - 1) + fib(n - 2); }\n" +
            "fn fact(n: u32) u32 { var r: u32 = 1; var i: u32 = 2; while (i <= n) { r = r * i; i = i + 1; } return r; }\n" +
            "pub fn main() u8 {\n" +
            "    const a: u32 = comptime fib(10);\n" +            // 55
            "    const b: u32 = comptime fact(5);\n" +            // 120
            "    _ = printf(\"fib=%u fact=%u\\n\", a, b);\n" +
            "    return @intCast(a + b - 133);\n" +               // 55 + 120 - 133 = 42
            "}\n", 42, "fib=55 fact=120" },

        // Array-by-value return (the Milestone K cut, made sound). A `[N]T`-returning function copies
        // its result into a heap-owned buffer so the value outlives the call — Zig arrays are value
        // types. `squares()` is called TWICE: with the old dangling-stackalloc-pointer bug both `a`
        // and `b` would read garbage from the same dead frame, so two independent correct copies is
        // the regression guard. squares() = [0,1,4,9]; (1+4+9)+(1+4+9)+14 = 42.
        new object[] { "array_return",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "fn squares() [4]u32 {\n" +
            "    var t: [4]u32 = undefined;\n" +
            "    var i: usize = 0;\n" +
            "    while (i < 4) { t[i] = @intCast(i * i); i = i + 1; }\n" +
            "    return t;\n}\n" +
            "pub fn main() u8 {\n" +
            "    const a = squares();\n" +
            "    const b = squares();\n" +
            "    _ = printf(\"a3=%u b3=%u\\n\", a[3], b[3]);\n" +
            "    const s: u32 = a[1] + a[2] + a[3] + b[1] + b[2] + b[3] + 14;\n" +
            "    return @intCast(s);\n" +
            "}\n", 42, "a3=9 b3=9" },

        // Comptime aggregate — a comptime function returning a STRUCT by value (Milestone T). The
        // interpreter zero-fills `undefined`, runs the field stores + arithmetic, and splices the
        // result as a `new V { … }` initializer. Exercises passing args into the comptime call. Both
        // uses are LOCAL `const = comptime …` — the round-trippable form (real zig rejects `comptime`
        // on a container const as "already comptime"). G.sum = 21, L.sum = 10, +11 = 42.
        new object[] { "comptime_struct",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "const V = struct { a: u32, b: u32, sum: u32 };\n" +
            "fn mk(x: u32, y: u32) V {\n" +
            "    var v: V = undefined;\n" +
            "    v.a = x; v.b = y; v.sum = v.a + v.b;\n" +
            "    return v;\n}\n" +
            "pub fn main() u8 {\n" +
            "    const g = comptime mk(10, 11);\n" +
            "    const l = comptime mk(4, 6);\n" +
            "    _ = printf(\"g=%u l=%u\\n\", g.sum, l.sum);\n" +
            "    return @intCast(g.sum + l.sum + 11);\n" +
            "}\n", 42, "g=21 l=10" },

        // Comptime aggregate — a comptime function returning an ARRAY (a lookup table). The
        // interpreter zero-fills `undefined`, runs the fill loop (`t[i] = i*i`), and splices the
        // table as a `stackalloc u32[]{ 0, 1, 4, 9, 16 }` at the LOCAL use site (the round-trippable
        // form). The squares() function returns an array by value soundly (the array-return
        // increment). sum(0,1,4,9,16) = 30, + 12 = 42.
        new object[] { "comptime_table",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "fn squares() [5]u32 {\n" +
            "    var t: [5]u32 = undefined;\n" +
            "    var i: usize = 0;\n" +
            "    while (i < 5) { t[i] = @intCast(i * i); i = i + 1; }\n" +
            "    return t;\n}\n" +
            "pub fn main() u8 {\n" +
            "    const tbl = comptime squares();\n" +
            "    _ = printf(\"%u %u %u %u %u\\n\", tbl[0], tbl[1], tbl[2], tbl[3], tbl[4]);\n" +
            "    return @intCast(tbl[0] + tbl[1] + tbl[2] + tbl[3] + tbl[4] + 12);\n" +
            "}\n", 42, "0 1 4 9 16" },

        // `inline for (lo..hi) |i|` — comptime loop UNROLLING (Milestone T, part 3). The SAME
        // construct is exercised in both contexts: inside buildSquares() it runs at COMPTIME (the
        // function is `comptime`-called, so the interpreter walks the unrolled copies to fold the
        // table), and in main() it unrolls at RUNTIME into straight-line accumulation. sq = [0,1,4,9,16],
        // sum = 30; 30 + 12 = 42. Validated identical (stdout + exit) against real zig 0.17.
        new object[] { "inline_for",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "fn buildSquares() [5]u32 {\n" +
            "    var t: [5]u32 = undefined;\n" +
            "    inline for (0..5) |i| { t[i] = @intCast(i * i); }\n" +
            "    return t;\n}\n" +
            "pub fn main() u8 {\n" +
            "    const sq = comptime buildSquares();\n" +
            "    var sum: u32 = 0;\n" +
            "    inline for (0..5) |i| { sum += sq[i]; }\n" +
            "    _ = printf(\"sum=%u sq4=%u\\n\", sum, sq[4]);\n" +
            "    return @intCast(sum + 12);\n" +
            "}\n", 42, "sum=30 sq4=16" },

        // `inline for (arr) |x|` over a fixed array (Milestone T, part 3) — unrolls once per element,
        // binding `x` to each element by value. 3 + 7 + 11 + 21 = 42.
        new object[] { "inline_for_array",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 {\n" +
            "    const items = [_]u32{ 3, 7, 11, 21 };\n" +
            "    var sum: u32 = 0;\n" +
            "    inline for (items) |x| { sum += x; }\n" +
            "    _ = printf(\"sum=%u\\n\", sum);\n" +
            "    return @intCast(sum);\n" +
            "}\n", 42, "sum=42" },

        // `inline while (i < N) : (i = i + step)` over a `comptime var` counter (Milestone T, part 3) —
        // unrolls into straight-line accumulation. arr=[5,10,15,20] → sum 50; 50 - 8 = 42.
        new object[] { "inline_while",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 {\n" +
            "    const arr = [_]u32{ 5, 10, 15, 20 };\n" +
            "    var sum: u32 = 0;\n" +
            "    comptime var i: usize = 0;\n" +
            "    inline while (i < 4) : (i = i + 1) { sum += arr[i]; }\n" +
            "    _ = printf(\"sum=%u\\n\", sum);\n" +
            "    return @intCast(sum - 8);\n" +
            "}\n", 42, "sum=50" },

        // `comptime { … }` block STATEMENT (Milestone T, part 3) — runs at compile time, folding a
        // `while` that sums 1..8 (= 36) into the enclosing `comptime var total`; no runtime loop. 36 + 6 = 42.
        new object[] { "comptime_block",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 {\n" +
            "    comptime var total: u32 = 0;\n" +
            "    comptime {\n" +
            "        var i: u32 = 1;\n" +
            "        while (i <= 8) : (i = i + 1) { total = total + i; }\n" +
            "    }\n" +
            "    _ = printf(\"total=%u\\n\", @as(u32, total));\n" +
            "    return @intCast(total + 6);\n" +
            "}\n", 42, "total=36" },

        // `@alignOf(T)` / `@offsetOf(T, "field")` as comptime values (Milestone T, part 4). An
        // `extern struct` pins the C-ABI layout (a plain Zig struct may reorder fields), so dotcc's
        // layout model and real zig agree: Point { a:u8, b:u32, c:u16 } → size 12, align 4, b@4, c@8.
        // 12 + 4 + 4 + 8 + 14 = 42. (`@sizeOf` rides along — already supported.)
        new object[] { "align_offset",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "const Point = extern struct { a: u8, b: u32, c: u16 };\n" +
            "pub fn main() u8 {\n" +
            "    const sz: usize = @sizeOf(Point);\n" +
            "    const al: usize = @alignOf(Point);\n" +
            "    const ob: usize = @offsetOf(Point, \"b\");\n" +
            "    const oc: usize = @offsetOf(Point, \"c\");\n" +
            "    _ = printf(\"sz=%zu al=%zu ob=%zu oc=%zu\\n\", sz, al, ob, oc);\n" +
            "    return @intCast(sz + al + ob + oc + 14);\n" +
            "}\n", 42, "sz=12 al=4 ob=4 oc=8" },

        // `arr.len` on a fixed `[N]T` array — the comptime-known count N (folded to a literal, since a
        // fixed array lowers to a pointer with no runtime length field). arr=[10,20,30,40] → len 4,
        // sum 100; 4 + 100 - 62 = 42.
        new object[] { "array_len",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 {\n" +
            "    const arr = [_]u32{ 10, 20, 30, 40 };\n" +
            "    const n: usize = arr.len;\n" +
            "    var sum: u32 = 0;\n" +
            "    var i: usize = 0;\n" +
            "    while (i < arr.len) : (i = i + 1) { sum += arr[i]; }\n" +
            "    _ = printf(\"len=%zu sum=%u\\n\", n, sum);\n" +
            "    return @intCast(arr.len + sum - 62);\n" +
            "}\n", 42, "len=4 sum=100" },

        // Milestone X, part 1 — `@errorName(e)`: the un-erased error name as `[]const u8`. dotcc
        // carries the flat code→name table into the emit; `@errorName(error.Ok)` = "Ok". The exit is
        // content-sensitive (reads a name byte AND the length): @as(usize,name[0]) + name.len - 39 =
        // 79 ('O') + 2 - 39 = 42 (a wrong name byte or length diverges from real zig).
        new object[] { "error_name",
            "pub fn main() u8 {\n" +
            "    const name = @errorName(error.Ok);\n" +
            "    return @intCast(@as(usize, name[0]) + name.len - 39);\n" +
            "}\n", 42, "" },

        // Milestone X, part 2 — `E.member` (set-qualified error reference). `MyError.Boom` resolves
        // to the same flat code as the bare `error.Boom` (membership erased), as a compared value and
        // in `return` position. Content-sensitive: Boom==Boom (+20), Fizz==Fizz (+20), Boom!=Fizz (+1);
        // boom() returns MyError.Boom, caught → 1. 20 + 20 + 1 + 1 = 42.
        new object[] { "error_member",
            "const MyError = error{ Boom, Fizz };\n" +
            "fn boom() MyError!u8 { return MyError.Boom; }\n" +
            "pub fn main() u8 {\n" +
            "    var acc: u8 = 0;\n" +
            "    if (MyError.Boom == error.Boom) acc += 20;\n" +
            "    if (MyError.Fizz == error.Fizz) acc += 20;\n" +
            "    if (MyError.Boom != MyError.Fizz) acc += 1;\n" +
            "    const r = boom() catch 1;\n" +
            "    return acc + r;\n" +
            "}\n", 42, "" },

        // Milestone X, part 3 — error-set membership checking (the LEGAL side). Every returned error
        // is a member of the function's declared set `E` (bare `error.A` AND set-qualified `E.B`), so
        // dotcc accepts it exactly like real zig (the rejection of a FOREIGN error is a unit pin, not
        // an oracle case — both compilers reject it). pick(42) → 42; the error paths are exercised.
        new object[] { "error_set_check",
            "const E = error{ A, B };\n" +
            "fn pick(x: u8) E!u8 {\n" +
            "    if (x == 0) return error.A;\n" +
            "    if (x == 1) return E.B;\n" +
            "    return x;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const ok = pick(42) catch 0;\n" +
            "    _ = pick(0) catch 0;\n" +
            "    _ = pick(1) catch 0;\n" +
            "    return ok;\n" +
            "}\n", 42, "" },

        // Milestone X, part 3b — an error set as a plain VALUE type (param / non-`!T` return /
        // local) + an EXHAUSTIVE switch EXPRESSION with NO `else`. `worst()` returns the error
        // VALUE (not a `MathError!T`); `weight` takes the set as a param and switches over every
        // member with no else (real zig proves it exhaustive; dotcc injects the `_` default).
        // 20 (Overflow) + 10 (DivByZero) + 5 (Underflow) + 7 = 42.
        new object[] { "error_set_type",
            "const MathError = error{ DivByZero, Overflow, Underflow };\n" +
            "fn worst() MathError { return MathError.Overflow; }\n" +
            "fn weight(e: MathError) u8 {\n" +
            "    return switch (e) {\n" +
            "        error.DivByZero => 10,\n" +
            "        error.Overflow => 20,\n" +
            "        error.Underflow => 5,\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const e: MathError = worst();\n" +
            "    var acc: u8 = weight(e);\n" +
            "    acc += weight(MathError.DivByZero);\n" +
            "    acc += weight(error.Underflow);\n" +
            "    acc += 7;\n" +
            "    return acc;\n" +
            "}\n", 42, "" },

        // Milestone Y, part 1 — value-position `if`/`switch` with a block-bodied (labeled value-block)
        // branch. A multi-statement switch arm yields via `break :blk v`; dotcc lowers the whole
        // switch/if as a STATEMENT filling a result temp (not a C# switch-expression / ternary), which
        // a C# expression can't host. classify(1)=20 + classify(7)=7 + (if total>10 → 15) = 42.
        new object[] { "value_control_flow",
            "fn classify(n: i32) i32 {\n" +
            "    const label = switch (n) {\n" +
            "        0 => blk: {\n" +
            "            const hundred: i32 = 100;\n" +
            "            break :blk hundred + 1;\n" +
            "        },\n" +
            "        1, 2 => 20,\n" +
            "        else => blk: {\n" +
            "            var acc: i32 = 0;\n" +
            "            acc = acc + n;\n" +
            "            break :blk acc;\n" +
            "        },\n" +
            "    };\n" +
            "    return label;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var total: i32 = 0;\n" +
            "    total = total + classify(1);\n" +
            "    total = total + classify(7);\n" +
            "    const pick = if (total > 10) blk: {\n" +
            "        const bonus: i32 = 15;\n" +
            "        break :blk bonus;\n" +
            "    } else 0;\n" +
            "    total = total + pick;\n" +
            "    return @intCast(total);\n" +
            "}\n", 42, "" },

        // Milestone Y, part 2 — value-position loops (`while/for … else`) yielding via `break v`.
        // `whileVal` (unlabeled break-value, 20), `labeledWhileVal` (`break :outer v` from a nested
        // loop, 15), `forVal` (the for-over-slice search idiom, first element > 5 = 7). 20+15+7 = 42.
        new object[] { "value_loop",
            "fn whileVal() i32 {\n" +
            "    var i: i32 = 0;\n" +
            "    return while (i < 50) {\n" +
            "        i = i + 1;\n" +
            "        if (i == 20) break i;\n" +
            "    } else 0;\n" +
            "}\n" +
            "fn labeledWhileVal() i32 {\n" +
            "    var i: i32 = 0;\n" +
            "    return outer: while (i < 5) {\n" +
            "        var j: i32 = 0;\n" +
            "        while (j < 5) {\n" +
            "            if (i == 2 and j == 3) break :outer 15;\n" +
            "            j = j + 1;\n" +
            "        }\n" +
            "        i = i + 1;\n" +
            "    } else 0;\n" +
            "}\n" +
            "fn forVal(xs: []const i32) i32 {\n" +
            "    return for (xs) |x| {\n" +
            "        if (x > 5) break x;\n" +
            "    } else 0;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var arr = [_]i32{ 1, 2, 7, 9 };\n" +
            "    var total: i32 = 0;\n" +
            "    total = total + whileVal();\n" +
            "    total = total + labeledWhileVal();\n" +
            "    total = total + forVal(arr[0..]);\n" +
            "    return @intCast(total);\n" +
            "}\n", 42, "" },

        // Milestone Z — a MULTI-variant tagged-union capture prong `.circle, .square => |r|`. Both
        // variants carry i32, so `r` binds to the shared payload (they overlap at offset 0). area of a
        // circle(9) = 18 and a square(12) = 24; 18 + 24 = 42.
        new object[] { "union_multi_capture",
            "const Shape = union(enum) {\n" +
            "    circle: i32,\n" +
            "    square: i32,\n" +
            "    name: u8,\n" +
            "};\n" +
            "fn area(s: Shape) i32 {\n" +
            "    switch (s) {\n" +
            "        .circle, .square => |r| {\n" +
            "            return r * 2;\n" +
            "        },\n" +
            "        .name => |c| {\n" +
            "            return @as(i32, c);\n" +
            "        },\n" +
            "    }\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const a = Shape{ .circle = 9 };\n" +
            "    const b = Shape{ .square = 12 };\n" +
            "    return @intCast(area(a) + area(b));\n" +
            "}\n", 42, "" },

        // Milestone Z — `for (s, 0..) |*e, i|`: a BY-REFERENCE element capture WITH the usize index.
        // `e.* = e.* + i` adds each element's index in place: {10,10,10,10} -> {10,11,12,13} (sum 46);
        // 46 - 4 = 42.
        new object[] { "for_idx_byref",
            "fn scaleByIndex(xs: []i32) void {\n" +
            "    for (xs, 0..) |*e, i| {\n" +
            "        e.* = e.* + @as(i32, @intCast(i));\n" +
            "    }\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var arr = [_]i32{ 10, 10, 10, 10 };\n" +
            "    scaleByIndex(arr[0..]);\n" +
            "    var total: i32 = 0;\n" +
            "    for (arr[0..]) |x| {\n" +
            "        total = total + x;\n" +
            "    }\n" +
            "    return @intCast(total - 4);\n" +
            "}\n", 42, "" },

        // Milestone Z — `[N:s]T` array literals with NON-ZERO sentinels. The trailing slot holds `s`,
        // readable at index len (well-defined in both for a literal). a[3]=9 + b[2]=5 + b[0]=14 +
        // b[1]=14 = 42. (An `undefined` sentinel array's slot is dotcc-defined but zig-undefined, so
        // only the literal form is differentially tested.)
        new object[] { "sentinel_array_nonzero",
            "pub fn main() u8 {\n" +
            "    const a: [3:9]i32 = .{ 10, 11, 12 };\n" +
            "    const b: [2:5]i32 = .{ 14, 14 };\n" +
            "    var total: i32 = a[3];\n" +
            "    total = total + b[2];\n" +
            "    total = total + b[0] + b[1];\n" +
            "    return @intCast(total);\n" +
            "}\n", 42, "" },

        // `threadlocal var` (completion-milestone part 2, the C _Thread_local twofer) —
        // thread storage duration → [ThreadStatic]. Single-threaded observable (dotcc's
        // Zig subset has no std.Thread): the main thread reads/writes its own slot,
        // starting at the zero initial value every thread gets.
        new object[] { "threadlocal_var",
            "threadlocal var tl_count: i32 = 0;\n" +
            "pub fn main() u8 {\n" +
            "    tl_count = 40;\n" +
            "    tl_count = tl_count + 2;\n" +
            "    return @intCast(tl_count);\n" +
            "}\n", 42, "" },
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
            var emitted = Compiler.EmitCSharp(new[] { zigPath }, emit: EmitMode.Csproj);
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

    /// <summary>MIXED <c>.c</c> + <c>.zig</c> programs (Milestone V — C↔Zig shared-heap interop).
    /// Each case is a C translation unit + a Zig <c>main</c> + expected exit + expected stdout.
    /// The unifying guarantee under test: <c>std.heap.c_allocator</c> IS the C <c>malloc</c>/<c>free</c>/
    /// <c>realloc</c> heap, so a buffer allocated by one front-end can be read / freed / resized by the
    /// other, and a Zig function taking an opaque <c>std.mem.Allocator</c> works when its result crosses
    /// into C. dotcc lowers both files into one program (C first, then the Zig group); real zig builds the
    /// same input set (the <c>.c</c> listed alongside the root <c>.zig</c>, <c>-lc</c>). NOTE: only
    /// <c>c_allocator</c> is cross-seam-safe — real zig's <c>page_allocator</c> is mmap/VirtualAlloc, not
    /// malloc, so freeing its memory with C <c>free</c> would be UB; every program here uses
    /// <c>c_allocator</c> for memory that crosses the boundary.</summary>
    public static IEnumerable<object[]> MixedPrograms => new[]
    {
        // alloc/free across the seam: Zig allocates 4 ints through c_allocator, C reads (sum 100) then
        // frees them; C mallocs 10 ints, Zig reads (sum 55), C frees. One shared heap → 100+55-113 = 42.
        new object[] { "mixed_shared_heap",
            "#include <stdlib.h>\n" +
            "int *make_ints(int n) {\n" +
            "    int *p = malloc(n * sizeof(int));\n" +
            "    for (int i = 0; i < n; i++) { p[i] = i + 1; }\n" +
            "    return p;\n" +
            "}\n" +
            "int sum_ints(int *p, int n) {\n" +
            "    int s = 0;\n" +
            "    for (int i = 0; i < n; i++) { s += p[i]; }\n" +
            "    return s;\n" +
            "}\n" +
            "void take_and_free(int *p) { free(p); }\n",
            "const std = @import(\"std\");\n" +
            "extern fn make_ints(n: c_int) [*c]c_int;\n" +
            "extern fn sum_ints(p: [*c]c_int, n: c_int) c_int;\n" +
            "extern fn take_and_free(p: [*c]c_int) void;\n" +
            "pub fn main() u8 {\n" +
            "    const a = std.heap.c_allocator;\n" +
            "    const mine = a.alloc(c_int, 4) catch return 1;\n" +
            "    mine[0] = 10; mine[1] = 20; mine[2] = 30; mine[3] = 40;\n" +
            "    const zsum = sum_ints(mine.ptr, 4);\n" +
            "    take_and_free(mine.ptr);\n" +
            "    const raw = make_ints(10);\n" +
            "    var sum: c_int = 0;\n" +
            "    var i: usize = 0;\n" +
            "    while (i < 10) : (i = i + 1) { sum = sum + raw[i]; }\n" +
            "    take_and_free(raw);\n" +
            "    return @intCast(zsum + sum - 113);\n" +
            "}\n", 42, "" },
        // create / destroy / realloc across the seam: Zig c_allocator.create(c_int), C reads it (30), Zig
        // destroys; then alloc 2 → realloc 4 (prefix preserved) → sum 12. 30+12 = 42.
        new object[] { "mixed_create_realloc",
            "int read_int(int *p) { return *p; }\n",
            "const std = @import(\"std\");\n" +
            "extern fn read_int(p: *c_int) c_int;\n" +
            "pub fn main() u8 {\n" +
            "    const a = std.heap.c_allocator;\n" +
            "    const p = a.create(c_int) catch return 1;\n" +
            "    p.* = 30;\n" +
            "    const v = read_int(p);\n" +
            "    a.destroy(p);\n" +
            "    const s = a.alloc(c_int, 2) catch return 1;\n" +
            "    s[0] = 5; s[1] = 7;\n" +
            "    const s2 = a.realloc(s, 4) catch return 1;\n" +
            "    s2[2] = 0; s2[3] = 0;\n" +
            "    var sum: c_int = 0;\n" +
            "    for (s2) |x| { sum = sum + x; }\n" +
            "    a.free(s2);\n" +
            "    return @intCast(v + sum);\n" +
            "}\n", 42, "" },
        // "pass the correct std.mem.Allocator": a Zig fn takes an opaque std.mem.Allocator param (indirect
        // dispatch); main passes c_allocator; the allocated buffer crosses to C which sums it (36). 36+6 = 42.
        new object[] { "mixed_alloc_param",
            "int sum_ints(int *p, int n) {\n" +
            "    int s = 0;\n" +
            "    for (int i = 0; i < n; i++) { s += p[i]; }\n" +
            "    return s;\n" +
            "}\n",
            "const std = @import(\"std\");\n" +
            "extern fn sum_ints(p: [*c]c_int, n: c_int) c_int;\n" +
            "fn build(a: std.mem.Allocator, n: usize) ![]c_int {\n" +
            "    const s = try a.alloc(c_int, n);\n" +
            "    var i: usize = 0;\n" +
            "    while (i < n) : (i = i + 1) { s[i] = @intCast(i + 1); }\n" +
            "    return s;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const a = std.heap.c_allocator;\n" +
            "    const s = build(a, 8) catch return 1;\n" +
            "    const total = sum_ints(s.ptr, 8);\n" +
            "    a.free(s);\n" +
            "    return @intCast(total + 6);\n" +
            "}\n", 42, "" },
        // Milestone W, part 2 — a C `lua_Alloc` behind a Zig `std.mem.Allocator` (the deep bridge). A
        // C realloc-style allocator (`nsize==0` ⇒ free, else realloc; `ud` is a byte-counter) is
        // imported via `extern fn` and wrapped in a HAND-WRITTEN custom allocator: the 4-fn vtable's
        // alloc/free call the C fn-ptr across the seam, bound by bare name. alloc 10 (counter += 10),
        // fill 0..9 (sum 45), free → 45 + 10 - 13 = 42. Real zig needs the same explicit adapter (no
        // auto C-fn-ptr→Allocator coercion exists), so the SAME source is valid in both compilers.
        new object[] { "mixed_lua_alloc",
            "#include <stdlib.h>\n" +
            "#include <stddef.h>\n" +
            "void *lua_alloc(void *ud, void *ptr, size_t osize, size_t nsize) {\n" +
            "    (void)osize;\n" +
            "    if (nsize == 0) { free(ptr); return NULL; }\n" +
            "    if (ud != NULL) { *(size_t *)ud += nsize; }\n" +
            "    return realloc(ptr, nsize);\n" +
            "}\n",
            "const std = @import(\"std\");\n" +
            "extern fn lua_alloc(ud: ?*anyopaque, ptr: ?*anyopaque, osize: usize, nsize: usize) ?*anyopaque;\n" +
            "fn luaAlloc(ctx: *anyopaque, len: usize, alignment: std.mem.Alignment, ret_addr: usize) ?[*]u8 {\n" +
            "    _ = alignment; _ = ret_addr;\n" +
            "    return @ptrCast(lua_alloc(ctx, null, 0, len));\n" +
            "}\n" +
            "fn luaResize(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ret_addr: usize) bool {\n" +
            "    _ = ctx; _ = memory; _ = alignment; _ = new_len; _ = ret_addr;\n" +
            "    return false;\n" +
            "}\n" +
            "fn luaRemap(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ret_addr: usize) ?[*]u8 {\n" +
            "    _ = ctx; _ = memory; _ = alignment; _ = new_len; _ = ret_addr;\n" +
            "    return null;\n" +
            "}\n" +
            "fn luaFree(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ret_addr: usize) void {\n" +
            "    _ = alignment; _ = ret_addr;\n" +
            "    _ = lua_alloc(ctx, memory.ptr, memory.len, 0);\n" +
            "}\n" +
            "const lua_vtable = std.mem.Allocator.VTable{ .alloc = luaAlloc, .resize = luaResize, .remap = luaRemap, .free = luaFree };\n" +
            "pub fn main() u8 {\n" +
            "    var bytes: usize = 0;\n" +
            "    const a = std.mem.Allocator{ .ptr = &bytes, .vtable = &lua_vtable };\n" +
            "    const buf = a.alloc(u8, 10) catch return 1;\n" +
            "    var i: usize = 0;\n" +
            "    while (i < 10) : (i = i + 1) { buf[i] = @intCast(i); }\n" +
            "    var sum: usize = 0;\n" +
            "    i = 0;\n" +
            "    while (i < 10) : (i = i + 1) { sum += buf[i]; }\n" +
            "    a.free(buf);\n" +
            "    return @intCast(sum + bytes - 13);\n" +
            "}\n", 42, "" },
    };

    /// <summary>Differential for a MIXED <c>.c</c> + <c>.zig</c> program: dotcc lowers both files into one
    /// C# program (<see cref="Compiler.EmitCSharp"/> over the pair) and runs it; real zig builds the same
    /// input set and runs it; the exit code AND stdout must agree (and match the expected). The mixed
    /// analogue of <see cref="Dotcc_matches_zig"/>; see <see cref="MixedPrograms"/> for the guarantee.</summary>
    [Theory]
    [MemberData(nameof(MixedPrograms))]
    public void Dotcc_matches_zig_mixed(string name, string cSource, string zigSource, int expectedExit, string expectedStdout)
    {
        if (!ZigRunRequested)
        {
            Assert.Skip(
                $"Zig oracle is opt-in. Set {RunZigEnv}=1 to compile + run each mixed .c+.zig program " +
                $"with the real zig compiler and assert dotcc's mixed path agrees.");
        }
        if (!ZigOracle.IsAvailable)
        {
            Assert.Skip($"{RunZigEnv} requested but no `zig` is on PATH on this host.");
        }

        // dotcc path: write extra.c + main.zig to a temp dir, emit C# over BOTH (csproj-shaped —
        // Roslyn rejects the #:property header), compile in-process, run, capture exit + stdout.
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zig-mixed-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var cPath = Path.Combine(dir, "extra.c");
        var zigPath = Path.Combine(dir, "main.zig");
        File.WriteAllText(cPath, cSource);
        File.WriteAllText(zigPath, zigSource);
        int dotccExit;
        string dotccStdout;
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { cPath, zigPath }, emit: EmitMode.Csproj);
            (dotccStdout, dotccExit) = FixtureRunner.CompileAndRunCapturingExit(emitted, Array.Empty<string>());

            // zig path: build + run the SAME input set (CompileAndRun lists the sibling .c alongside
            // the root .zig and links -lc, so std.heap.c_allocator and C malloc share one heap).
            var (zigStdout, zigExit) = ZigOracle.CompileAndRun(zigPath, dir);

            dotccExit.ShouldBe(zigExit, $"dotcc's mixed path diverges from real zig on '{name}' (exit code)");
            dotccExit.ShouldBe(expectedExit, $"'{name}' did not produce the expected exit code");
            Norm(dotccStdout).ShouldBe(Norm(zigStdout), $"dotcc's mixed path diverges from real zig on '{name}' (stdout)");
            Norm(dotccStdout).ShouldBe(expectedStdout, $"'{name}' did not produce the expected stdout");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
