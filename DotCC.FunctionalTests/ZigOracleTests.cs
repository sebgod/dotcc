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
    /// <remarks>This host's rows of <see cref="AllPrograms"/> (<see cref="TestShard"/>).</remarks>
    public static IEnumerable<object[]> Programs => TestShard.Rows(AllPrograms);

    private static IEnumerable<object[]> AllPrograms => new[]
    {
        new object[] { "arith",
            "pub fn main() u8 { const x: u8 = 40; return x + 2; }\n", 42, "" },
        new object[] { "if_expr",
            "pub fn main() u8 { const x: u8 = 40; const y: u8 = if (x > 10) x else 0; return y + 2; }\n", 42, "" },
        new object[] { "if_stmt",
            "pub fn main() u8 { var x: u8 = 0; if (3 > 2) { x = 42; } else { x = 1; } return x; }\n", 42, "" },
        // if_capture_expr — a VALUE-position captured if `if (opt) |x| thenE else elseE` (S4a). Binds
        // the payload in the then-branch; the null path takes the else. pick(41)=42, pick(null)=0 → 42.
        new object[] { "if_capture_expr",
            "fn pick(opt: ?u8) u8 { return if (opt) |x| x + 1 else 0; }\n" +
            "pub fn main() u8 { return pick(41) + pick(null); }\n", 42, "" },
        new object[] { "while_sum",
            "pub fn main() u8 { var i: u8 = 0; var sum: u8 = 0; while (i < 5) { sum = sum + i; i = i + 1; } return sum; }\n", 10, "" },
        new object[] { "bitnot",
            "pub fn main() u8 { const a: u8 = 0; const b: u8 = ~a; return b; }\n", 255, "" },
        // A statement `switch` whose prongs `return` (road-to-zig-std S9 — the enum/int classify shape),
        // with range case-values. classify('5')=1, classify('x')=2, classify('!')=0 → 3.
        new object[] { "switch_return_prongs",
            "fn classify(c: u8) u8 {\n" +
            "    switch (c) {\n" +
            "        '0'...'9' => return 1,\n" +
            "        'a'...'z' => return 2,\n" +
            "        else => return 0,\n" +
            "    }\n" +
            "}\n" +
            "pub fn main() u8 { return classify('5') + classify('x') + classify('!'); }\n", 3, "" },
        // A tagged-union `switch` whose prongs `return` (no capture) — the union analogue of the above.
        // area(circle)=3, area(square)=4 → 7.
        new object[] { "union_switch_return_prongs",
            "const Shape = union(enum) { circle: u8, square: u8 };\n" +
            "fn area(s: Shape) u8 {\n" +
            "    switch (s) {\n" +
            "        .circle => return 3,\n" +
            "        .square => return 4,\n" +
            "    }\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const c = Shape{ .circle = 1 };\n" +
            "    const sq = Shape{ .square = 1 };\n" +
            "    return area(c) + area(sq);\n" +
            "}\n", 7, "" },
        // A tagged-union `switch` whose prongs capture the payload AND `return` it (`|x| return e`)
        // or evaluate a bare expr with a capture (`|x| e`) — the capture-BODY prong forms
        // (road-to-zig-std S9, ProngCaptureReturn/ProngCaptureExpr), no braces. get(n=10)=11,
        // get(m=20)=22 → 33.
        new object[] { "union_switch_capture_body_prongs",
            "const Val = union(enum) { n: u8, m: u8 };\n" +
            "fn get(v: Val) u8 {\n" +
            "    switch (v) {\n" +
            "        .n => |x| return x + 1,\n" +
            "        .m => |x| return x + 2,\n" +
            "    }\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const a = Val{ .n = 10 };\n" +
            "    const b = Val{ .m = 20 };\n" +
            "    return get(a) + get(b);\n" +
            "}\n", 33, "" },
        // `@intFromBool` in the exact shape std.ascii.toLower uses it: 'A'(65) is upper, so the mask is
        // 1<<5 = 32, and 65 | 32 = 97 ('a'). Proves the builtin matches real zig.
        new object[] { "int_from_bool",
            "pub fn main() u8 { const c: u8 = 65; const mask: u8 = @as(u8, @intFromBool(c >= 65 and c <= 90)) << 5; return c | mask; }\n", 97, "" },
        // i64 parameters: `wide` is type-checked by dotcc's emit + Roslyn with the
        // wider signedness the UsualArithmetic fix preserves; main is the observable.
        new object[] { "i64_params",
            "fn wide(a: i64, b: i64) i64 { return a * b; }\npub fn main() u8 { return 42; }\n", 42, "" },
        // Arbitrary-width ints `uN`/`iN` (road-to-zig-std B3) → smallest containing std width. Values
        // chosen to fit (no wrap): u4 5+3=8, u12 100, i7 -3; 8 + 100 - 3 = 105.
        new object[] { "arbitrary_width_ints",
            "pub fn main() u8 {\n" +
            "    var x: u4 = 5;\n" +
            "    x += 3;\n" +
            "    const y: u12 = 100;\n" +
            "    const z: i7 = -3;\n" +
            "    return @intCast(@as(i32, x) + y + z);\n" +
            "}\n", 105, "" },
        // Math builtins (road-to-zig-std B3) → ZigMath helpers. min 3, max 2, mod(-7,3)=2 (floored),
        // divFloor(-7,3)=-3, rem(7,3)=1, divTrunc(7,3)=2, popCount(0b1011)=3. 3+2+2-3+1+2+3+4 = 14.
        new object[] { "math_builtins",
            "pub fn main() u8 {\n" +
            "    const mn = @as(i32, @min(3, 7));\n" +
            "    const mx = @as(i32, @max(1, 2));\n" +
            "    const md = @as(i32, @mod(@as(i8, -7), 3));\n" +
            "    const df = @as(i32, @divFloor(@as(i8, -7), 3));\n" +
            "    const rm = @as(i32, @rem(@as(i8, 7), 3));\n" +
            "    const dt = @as(i32, @divTrunc(@as(i8, 7), 3));\n" +
            "    const pc = @as(i32, @popCount(@as(u8, 11)));\n" +
            "    return @intCast(mn + mx + md + df + rm + dt + pc + 4);\n" +
            "}\n", 14, "" },
        // @clz/@ctz (bit-zero counts within the type width) + @intFromPtr (address → usize; used as a
        // DETERMINISTIC pointer difference). clz(0b00010000 in u8)=3, ctz=4, &a[1]-&a[0]=1 → 3+4+1 = 8.
        new object[] { "bit_ptr_builtins",
            "pub fn main() u8 {\n" +
            "    const x: u8 = 0b00010000;\n" +
            "    const lz = @clz(x);\n" +
            "    const tz = @ctz(x);\n" +
            "    var a = [_]u8{ 0, 0, 0 };\n" +
            "    const d = @intFromPtr(&a[1]) - @intFromPtr(&a[0]);\n" +
            "    return @intCast(@as(u32, lz) + tz + d);\n" +
            "}\n", 8, "" },
        // @byteSwap (reverse byte order) + @abs (magnitude → the operand's UNSIGNED peer type).
        // byteSwap(0x0102 u16)=0x0201, &0xFF=1; @abs(i8 -5)=5, @abs(i32 -100)=100. 1+5+100-100 = 6.
        new object[] { "byteswap_abs_builtins",
            "pub fn main() u8 {\n" +
            "    const bs: u16 = @byteSwap(@as(u16, 0x0102));\n" +
            "    const a1: u32 = @abs(@as(i8, -5));\n" +
            "    const a2: u32 = @abs(@as(i32, -100));\n" +
            "    return @intCast((bs & 0xFF) + a1 + a2 - 100);\n" +
            "}\n", 6, "" },
        // overflow_builtins — @addWithOverflow/@subWithOverflow/@mulWithOverflow/@shlWithOverflow
        // (road-to-zig-std B3) return `struct { T, u1 }`, destructured (`const r1, const o1 = …`) or
        // indexed (`t[1]`). u8: 200+100 -> .{44,1}; 5-10 -> .{251,1}; 20*20 -> .{144,1};
        // 3<<7=384 -> .{128,1}; i8: 100+100 -> .{-56,1}. flags = 1+1+1+1 (=4) + signed 1 = 5; 44+5 = 49.
        new object[] { "overflow_builtins",
            "pub fn main() u8 {\n" +
            "    const r1, const o1 = @addWithOverflow(@as(u8, 200), 100);\n" +
            "    const sub = @subWithOverflow(@as(u8, 5), 10);\n" +
            "    const mul = @mulWithOverflow(@as(u8, 20), 20);\n" +
            "    const shl = @shlWithOverflow(@as(u8, 3), 7);\n" +
            "    const sadd = @addWithOverflow(@as(i8, 100), 100);\n" +
            "    const flags: u8 = @as(u8, o1) + @as(u8, sub[1]) + @as(u8, mul[1]) + @as(u8, shl[1]) + @as(u8, sadd[1]);\n" +
            "    return r1 + flags;\n" +
            "}\n", 49, "" },
        // A function CALL — main invokes a named function with arguments.
        new object[] { "call",
            "fn add(a: u8, b: u8) u8 { return a + b; }\npub fn main() u8 { return add(40, 2); }\n", 42, "" },
        // A FORWARD-referenced call — `add` is defined AFTER `main` (Zig has no
        // prototypes); the two-pass lowering must resolve it.
        new object[] { "call_forward",
            "pub fn main() u8 { return add(40, 2); }\nfn add(a: u8, b: u8) u8 { return a + b; }\n", 42, "" },
        // An INLINE named-field struct TYPE in a return-type slot (road-to-zig-std S9, grammar #90) —
        // reified as a synthesized nominal type, built via `.{ … }`, read back with `p.field`.
        // make() = {a:3, b:4}; 3 + 4 = 7.
        new object[] { "inline_struct_return_type",
            "fn make() struct { a: u8, b: u8 } { return .{ .a = 3, .b = 4 }; }\n" +
            "pub fn main() u8 { const p = make(); return p.a + p.b; }\n", 7, "" },
        // A NESTED `const Inner = struct {…};` inside a struct body (road-to-zig-std S9, grammar #89) —
        // registered under a parent-mangled name, resolved by plain name inside the parent's method,
        // built via `.{…}` and read with `i.field`. sum() = {x:3,y:4}; 3 + 4 = 7.
        new object[] { "nested_container_struct_member",
            "const Outer = struct {\n" +
            "    const Inner = struct { x: u8, y: u8 };\n" +
            "    fn sum() u8 {\n" +
            "        const i = Inner{ .x = 3, .y = 4 };\n" +
            "        return i.x + i.y;\n" +
            "    }\n" +
            "};\n" +
            "pub fn main() u8 { return Outer.sum(); }\n", 7, "" },
        // Comptime string concat `++` folded to a single string literal (road-to-zig-std S9). s = "abcd",
        // t = "xyz"; ('d'-'a') + ('z'-'x') = 3 + 2 = 5. (The `**` repeat fold is exercised by the always-on
        // unit pin only — the pinned oracle zig 0.17.0-dev.667 tokenizes `**` as `*` `*` and rejects the
        // syntax outright, even for canonical `"=" ** 5`, so it can't validate a `**` program.)
        new object[] { "string_concat",
            "pub fn main() u8 {\n" +
            "    const s = \"ab\" ++ \"cd\";\n" +
            "    const t = \"xy\" ++ \"z\";\n" +
            "    return s[3] - s[0] + t[2] - t[0];\n" +
            "}\n", 5, "" },
        // Comptime ARRAY-literal concat `++` folded to one array literal over the merged elements
        // (road-to-zig-std S9). a = {1,2,3,4}; a[0] + a[3] = 1 + 4 = 5. (Array `**` repeat, like string
        // `**`, can't be oracle-tested — zig 0.17.0-dev.667 rejects `**` syntax; it has an always-on pin.)
        new object[] { "array_literal_concat",
            "pub fn main() u8 {\n" +
            "    const a = [_]u8{ 1, 2 } ++ [_]u8{ 3, 4 };\n" +
            "    return a[0] + a[3];\n" +
            "}\n", 5, "" },
        // Comptime-CONST string operands (road-to-zig-std S5 seed): `prefix` (a const string) and a
        // parenthesized chained `++` both resolve to comptime values and fold. msg = "abcd", chain = "xyz";
        // ('d'-'a') + ('z'-'x') = 3 + 2 = 5.
        new object[] { "comptime_const_concat",
            "const prefix = \"ab\";\n" +
            "pub fn main() u8 {\n" +
            "    const msg = prefix ++ \"cd\";\n" +
            "    const chain = (\"x\" ++ \"y\") ++ \"z\";\n" +
            "    return msg[3] - msg[0] + chain[2] - chain[0];\n" +
            "}\n", 5, "" },
        // A comptime-CONST ARRAY operand (road-to-zig-std S5): `base` (a const array) folds into a `++`
        // with a literal. a = {1,2,3,4}; a[0] + a[3] = 1 + 4 = 5.
        new object[] { "comptime_const_array_concat",
            "const base = [_]u8{ 1, 2 };\n" +
            "pub fn main() u8 {\n" +
            "    const a = base ++ [_]u8{ 3, 4 };\n" +
            "    return a[0] + a[3];\n" +
            "}\n", 5, "" },
        // `@typeName(T)` for a primitive + a composed slice (road-to-zig-std S5 reflection): the source
        // spelling matches zig byte-for-byte. a = "u8", b = "[]const u8"; 'u'(117) + '8'(56) - '['(91) = 82.
        new object[] { "typename_primitive",
            "pub fn main() u8 {\n" +
            "    const a = @typeName(u8);\n" +
            "    const b = @typeName([]const u8);\n" +
            "    return a[0] + a[1] - b[0];\n" +
            "}\n", 82, "" },
        // An anon `.{…}` `++` operand borrows the element type from the typed operand (road-to-zig-std S5).
        // a = {1,2,3,4}; a[0] + a[3] = 1 + 4 = 5.
        new object[] { "anon_init_concat",
            "pub fn main() u8 {\n" +
            "    const a = [_]u8{ 1, 2 } ++ .{ 3, 4 };\n" +
            "    return a[0] + a[3];\n" +
            "}\n", 5, "" },
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
        // Fn-pointer TYPES with UNNAMED params (`fn (i32, i32) i32`) and an ERROR-UNION return
        // (`fn (i32) E!i32`), alongside the named form. f(10,5)=15, g(3)=-3, h(4) catch 0 = 8 → 20.
        new object[] { "fn_ptr_unnamed_err",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "const E = error{ Bad };\n" +
            "fn add(a: i32, b: i32) i32 { return a + b; }\n" +
            "fn neg(x: i32) i32 { return -x; }\n" +
            "fn checked(x: i32) E!i32 { if (x < 0) return error.Bad; return x * 2; }\n" +
            "pub fn main() u8 {\n" +
            "    const f: *const fn (i32, i32) i32 = &add;\n" +
            "    const g: *const fn (x: i32) i32 = &neg;\n" +
            "    const h: *const fn (i32) E!i32 = &checked;\n" +
            "    const r = f(10, 5) + g(3) + (h(4) catch 0);\n" + // 15 + -3 + 8 = 20
            "    _ = printf(\"r=%d\\n\", r);\n" +
            "    return @intCast(r);\n" +
            "}\n", 20, "r=20" },
        // Fn-pointer GLOBALS — typed + INFERRED — that FORWARD-REFERENCE a function declared later
        // (functions are registered before globals). handler(10)=20, alias(11)=22 → 42.
        new object[] { "fn_ptr_global",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "const handler: *const fn (i32) i32 = &laterFn;\n" +
            "const alias = &laterFn;\n" +
            "fn laterFn(x: i32) i32 { return x * 2; }\n" +
            "pub fn main() u8 {\n" +
            "    _ = printf(\"%d %d\\n\", handler(10), alias(11));\n" +
            "    return @intCast(handler(10) + alias(11));\n" +
            "}\n", 42, "20 22" },
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
        // The IDIOMATIC compound-assign continue `: (i += 1)` — the canonical Zig loop. The continue
        // clause now accepts the full assignment-operator set (not just plain `=`), so this parses and
        // must match real zig exactly. Mirrors loop_while_cont with `+=`: sum 0..4 = 10.
        new object[] { "loop_compound_cont",
            "pub fn main() u8 { var sum: u8 = 0; var i: u8 = 0; " +
            "while (i < 5) : (i += 1) { sum += i; } return sum; }\n", 10, "" },
        // A non-additive compound op in the continue (`p <<= 1`): p walks 1,2,4,8,16 (5 iterations
        // while p <= 16), the body counting each → 5. Exercises a shift-assign continue vs real zig.
        new object[] { "loop_shift_cont",
            "pub fn main() u8 { var n: u8 = 0; var p: u8 = 1; " +
            "while (p <= 16) : (p <<= 1) { n = n + 1; } return n; }\n", 5, "" },
        // road-to-zig-std S9 — a `test "…" {}` block and a container-level `comptime {}` block are
        // analysis-only; dotcc parses and DROPS both. Built as an EXECUTABLE, real zig likewise never
        // runs the test and evaluates the side-effect-free comptime block silently, so exit + stdout
        // match exactly: main returns 42.
        new object[] { "test_and_comptime_dropped",
            "test \"never runs in an exe\" { const a: i32 = 1; _ = a; }\n" +
            "comptime { const c: i32 = 2; _ = c; }\n" +
            "pub fn main() u8 { return 42; }\n", 42, "" },
        // road-to-zig-std S9 — a struct field DEFAULT (`n: i32 = 7`), NON-ZERO, is materialized when a
        // `.{…}` literal omits it. `.{ .m = 3 }` fills n=7 → 10, matching real zig (C#'s zero-init would
        // wrongly give 3). Exercises the default-fill in BuildStructInit vs the oracle.
        new object[] { "struct_field_default",
            "const S = struct { n: i32 = 7, m: i32 };\n" +
            "pub fn main() u8 { const s: S = .{ .m = 3 }; return @intCast(s.n + s.m); }\n", 10, "" },
        // road-to-zig-std S9 — a quoted identifier `@"a-b"` (Zig's reserved-word / arbitrary-string
        // escape hatch) lexes to a plain IDENT and mangles to the C# name `a_b`. Real zig accepts the
        // same declaration and use, so exit matches: 42.
        new object[] { "quoted_ident",
            "pub fn main() u8 { const @\"a-b\": u8 = 42; return @\"a-b\"; }\n", 42, "" },
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
        // resize_remap_fba — in-place resize/remap on a PROVABLE FixedBufferAllocator (the only wired
        // path; the C-heap / opaque forms stay a loud deferred error, their result being
        // page-dependent). `remap` grows the last allocation (same pointer, contents preserved) →
        // `?[]u8` consumed by `orelse`; `resize` shrinks the last allocation in place → `bool`.
        // 3*4 + 15 + 15 = 42.
        new object[] { "resize_remap_fba",
            "const std = @import(\"std\");\n" +
            "fn run() !u8 {\n" +
            "    var buffer: [64]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.init(&buffer);\n" +
            "    const a = fba.allocator();\n" +
            "    var s = try a.alloc(u8, 4);\n" +
            "    var i: usize = 0;\n" +
            "    while (i < 4) : (i += 1) { s[i] = 3; }\n" +
            "    s = a.remap(s, 6) orelse return 2;\n" +
            "    if (s.len != 6) return 3;\n" +
            "    s[4] = 15;\n" +
            "    s[5] = 15;\n" +
            "    var sum: u8 = 0;\n" +
            "    var j: usize = 0;\n" +
            "    while (j < s.len) : (j += 1) { sum += s[j]; }\n" +
            "    if (!a.resize(s, 3)) return 4;\n" +
            "    return sum;\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n", 42, "" },
        // opaque_resize_remap — the SAME resize/remap dance, but through an OPAQUE `std.mem.Allocator`
        // parameter (indirect vtable dispatch, not a devirt'd FBA). Backed by an FBA in main so the
        // in-place answer is deterministic and matches real zig byte-for-byte. 3*4 + 15 + 15 = 42.
        new object[] { "opaque_resize_remap",
            "const std = @import(\"std\");\n" +
            "fn run(a: std.mem.Allocator) !u8 {\n" +
            "    var s = try a.alloc(u8, 4);\n" +
            "    var i: usize = 0;\n" +
            "    while (i < 4) : (i += 1) { s[i] = 3; }\n" +
            "    s = a.remap(s, 6) orelse return 2;\n" +
            "    if (s.len != 6) return 3;\n" +
            "    s[4] = 15;\n" +
            "    s[5] = 15;\n" +
            "    var sum: u8 = 0;\n" +
            "    var j: usize = 0;\n" +
            "    while (j < s.len) : (j += 1) { sum += s[j]; }\n" +
            "    if (!a.resize(s, 3)) return 4;\n" +
            "    return sum;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var buffer: [64]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.init(&buffer);\n" +
            "    return run(fba.allocator()) catch 1;\n" +
            "}\n", 42, "" },
        // TUPLES (Milestone G). The headline use: a function returns a tuple `struct { u8, u8 }`
        // and the caller destructures it with `const a, const b = mm();` → C# ValueTuple +
        // `.Item1`/`.Item2`. 20 + 22 = 42.
        new object[] { "tuple_return",
            "fn mm() struct { u8, u8 } { return .{ 20, 22 }; }\n" +
            "pub fn main() u8 { const a, const b = mm(); return a + b; }\n", 42, "" },
        // A tuple LITERAL bound to a var, indexed by literal subscript (`t[0]`/`t[1]` → `.ItemN`).
        new object[] { "tuple_index",
            "pub fn main() u8 { const t = .{ @as(u8, 20), @as(u8, 22) }; return t[0] + t[1]; }\n", 42, "" },
        // An empty tuple `.{}` (non-generic ValueTuple) and an arity-9 tuple (> 7 → ValueTuple TRest
        // nesting; an index ≥ 7 reads through `.Rest`). 1 + 7 + 8 + 9 = 25.
        new object[] { "tuple_empty_and_wide",
            "pub fn main() u8 {\n" +
            "    const e = .{};\n" +
            "    _ = e;\n" +
            "    const t = .{ @as(u8, 1), @as(u8, 2), @as(u8, 3), @as(u8, 4), @as(u8, 5), @as(u8, 6), @as(u8, 7), @as(u8, 8), @as(u8, 9) };\n" +
            "    return t[0] + t[6] + t[7] + t[8];\n" + // 1 + 7 + 8 + 9 = 25
            "}\n", 25, "" },
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
        // A hex float `0x1.8p3` (= 12.0, no C# syntax → decimal), an underscored decimal float, and
        // exponent-only floats with no fraction dot (`1e3`, `4E2` — Zig allows them; both e/E spellings).
        new object[] { "lexer_floats",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 {\n" +
            "    const hf: f64 = 0x1.8p3;\n" + // 12.0
            "    const df: f64 = 1_5.0;\n" +   // 15.0
            "    const ef: f64 = 1e3;\n" +     // 1000.0 (exponent-only, lowercase e)
            "    const eg: f64 = 4E2;\n" +     // 400.0  (exponent-only, uppercase E)
            "    _ = printf(\"%.1f %.1f %.1f %.1f\\n\", hf, df, ef, eg);\n" +
            "    return 42;\n" +
            "}\n", 42, "12.0 15.0 1000.0 400.0" },
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

        // Curated std.mem helpers + the @memcpy/@memset builtins over slices, plus the `&array`
        // (`*[N]T` → `[]T`) slice coercion they rely on. eql (equal / not-equal), copyForwards,
        // @memset (fill), @memcpy (copy). Exits 42.
        new object[] { "std_mem_basic",
            "const std = @import(\"std\");\n" +
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "fn countL(s: []const u8) usize { return s.len; }\n" +
            "pub fn main() u8 {\n" +
            "    const a = [_]u8{ 1, 2, 3 };\n" +
            "    const b = [_]u8{ 1, 2, 3 };\n" +
            "    const c = [_]u8{ 1, 2, 4 };\n" +
            "    const eqAB: c_int = if (std.mem.eql(u8, &a, &b)) 1 else 0;\n" +
            "    const eqAC: c_int = if (std.mem.eql(u8, &a, &c)) 1 else 0;\n" +
            "    var dst = [_]u8{ 0, 0, 0 };\n" +
            "    std.mem.copyForwards(u8, &dst, &a);\n" +
            "    var buf = [_]u8{ 9, 9, 9, 9 };\n" +
            "    @memset(&buf, 7);\n" +
            "    var b2 = [_]u8{ 0, 0, 0 };\n" +
            "    @memcpy(&b2, &a);\n" +
            "    _ = printf(\"eql=%d%d len=%d copy=%d%d%d set=%d cpy=%d%d%d\\n\", eqAB, eqAC, @as(c_int, @intCast(countL(&a))), @as(c_int, dst[0]), @as(c_int, dst[1]), @as(c_int, dst[2]), @as(c_int, buf[0]), @as(c_int, b2[0]), @as(c_int, b2[1]), @as(c_int, b2[2]));\n" +
            "    return 42;\n" +
            "}\n", 42, "eql=10 len=3 copy=123 set=7 cpy=123" },

        // std.mem.span (NUL-sentinel `[*:0]const u8` C-string → a `[]const u8` slice, length excludes
        // the sentinel) and std.mem.zeroes (an all-zero scalar + struct). Exits 42.
        new object[] { "std_mem_span_zeroes",
            "const std = @import(\"std\");\n" +
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "const Point = struct { x: i32, y: i32 };\n" +
            "pub fn main() u8 {\n" +
            "    const p: [*:0]const u8 = \"hello\";\n" +
            "    const s = std.mem.span(p);\n" +
            "    const zi: i32 = std.mem.zeroes(i32);\n" +
            "    const zp = std.mem.zeroes(Point);\n" +
            "    _ = printf(\"span len=%d first=%c zeroes i=%d px=%d py=%d\\n\", @as(c_int, @intCast(s.len)), @as(c_int, s[0]), zi, zp.x, zp.y);\n" +
            "    return 42;\n" +
            "}\n", 42, "span len=5 first=h zeroes i=0 px=0 py=0" },

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

        // While-story completion: a capture-while `else` (runs on natural exit), a capture-while with
        // a continue-expression `: (cont)`, an error-union capture-while `else |e|`, and a for-slice
        // with a NON-ZERO index start `for (s, 5..)`. Exits 42.
        new object[] { "while_completion",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "const E = error{ Stop };\n" +
            "fn nextLT(i: *i32, n: i32) ?i32 { if (i.* < n) { i.* += 1; return i.*; } return null; }\n" +
            "fn step(i: *i32) E!i32 { if (i.* < 3) { i.* += 1; return i.*; } return error.Stop; }\n" +
            "pub fn main() u8 {\n" +
            "    var a: i32 = 0; var s1: i32 = 0;\n" +
            "    while (nextLT(&a, 3)) |v| { s1 += v; } else { s1 += 100; }\n" +      // 6 + 100 = 106
            "    var b: i32 = 0; var cnt: i32 = 0; var s2: i32 = 0;\n" +
            "    while (nextLT(&b, 4)) |v| : (cnt = cnt + 1) { s2 += v; }\n" +         // s2=10 cnt=4
            "    var c: i32 = 0; var s3: i32 = 0; var code: i32 = 0;\n" +
            "    while (step(&c)) |v| { s3 += v; } else |e| { code = if (e == error.Stop) 9 else 1; }\n" + // s3=6 code=9
            "    const arr = [_]u8{ 10, 20, 30 };\n" +
            "    var acc: i32 = 0;\n" +
            "    for (arr[0..], 5..) |x, idx| { acc += @as(i32, x) + @as(i32, @intCast(idx)); }\n" + // 78
            "    _ = printf(\"s1=%d s2=%d cnt=%d s3=%d code=%d acc=%d\\n\", s1, s2, cnt, s3, code, acc);\n" +
            "    return 42;\n" +
            "}\n", 42, "s1=106 s2=10 cnt=4 s3=6 code=9 acc=78" },

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
        // ANF (sub-expression positions): a `catch` with a side-effecting fallback and an
        // `orelse return` used INSIDE a larger expression (not a full RHS) — hoisted to a temp before
        // the enclosing statement (decl-init / assignment / return). a=2+20=22, b=1+5=6, c=g(-1)=7 → 35.
        new object[] { "anf_subexpr",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "const E = error{ Bad };\n" +
            "fn mk(ok: bool) E!i32 { if (ok) return 5; return error.Bad; }\n" +
            "fn dflt() i32 { return 20; }\n" +
            "fn find(x: i32) ?i32 { if (x > 0) return x; return null; }\n" +
            "fn g(x: i32) i32 { return 100 + (find(x) orelse return 7); }\n" +
            "pub fn main() u8 {\n" +
            "    const a: i32 = 2 + (mk(false) catch dflt());\n" + // decl-init sub-expr catch: 22
            "    var b: i32 = 0;\n" +
            "    b = 1 + (mk(true) catch dflt());\n" +             // assign sub-expr catch: 6
            "    const c: i32 = g(-1);\n" +                        // orelse return 7 → 7
            "    _ = printf(\"a=%d b=%d c=%d\\n\", a, b, c);\n" +
            "    return @as(u8, @intCast(a + b + c));\n" +          // 22 + 6 + 7 = 35
            "}\n", 35, "a=22 b=6 c=7" },
        // ANF Phase B — a value-position control-flow construct in a SUB-expression, made reachable by
        // the `( RhsExpr )` Primary + hoisted like Phase A: a block-bodied `if`, a `switch`-expression,
        // a labeled block, and a `for`-else loop, each inside `X + (…)`. 22 + 12 + 4 + 4 = 42.
        new object[] { "anf_value_controlflow_subexpr",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 {\n" +
            "    const c = true;\n" +
            "    const k: i32 = 1;\n" +
            "    const a: i32 = 2 + (if (c) blk: { break :blk @as(i32, 20); } else @as(i32, 0));\n" +
            "    const b: i32 = 5 + (switch (k) { 1 => @as(i32, 7), else => @as(i32, 0) });\n" +
            "    const d: i32 = 1 + (blk: { const t: i32 = 3; break :blk t; });\n" +
            "    const arr = [_]i32{ 1, 2, 3 };\n" +
            "    const e: i32 = for (arr[0..]) |x| { if (x == 2) break @as(i32, 4); } else @as(i32, 0);\n" +
            "    _ = printf(\"a=%d b=%d d=%d e=%d\\n\", a, b, d, e);\n" +
            "    return @intCast(a + b + d + e);\n" + // 22 + 12 + 4 + 4 = 42
            "}\n", 42, "a=22 b=12 d=4 e=4" },
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

        // A `[N:s]T` sentinel array GLOBAL — the pinned store reserves N+1 slots (like the local
        // stackalloc): a literal appends the sentinel, `undefined` reserves a zeroed slot. `g[3]`
        // reads the non-zero sentinel 9; `g.len` = 3 (excludes it). 1 + 9 + 5 = 15.
        new object[] { "sentinel_array_global",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "const g: [3:9]i32 = .{ 1, 2, 3 };\n" +
            "var z: [2:0]u8 = undefined;\n" +
            "pub fn main() u8 {\n" +
            "    z[0] = 5;\n" +
            "    _ = printf(\"g0=%d g3=%d len=%d z0=%d\\n\", g[0], g[3], @as(c_int, @intCast(g.len)), @as(c_int, z[0]));\n" +
            "    return @intCast(g[0] + g[3] + z[0]);\n" + // 1 + 9 + 5 = 15
            "}\n", 15, "g0=1 g3=9 len=3 z0=5" },

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

        // `pub`-wrapped container decls (struct / enum / union(enum)) — the modifier is peeled and
        // each lowers exactly like a bare container. p.x+p.y=30, Kind.b=1, Num{.i=12} switched = 12.
        new object[] { "pub_container",
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub const Point = struct { x: i32, y: i32 };\n" +
            "pub const Kind = enum { a, b, c };\n" +
            "pub const Num = union(enum) { i: i32, f: f32 };\n" +
            "pub fn main() u8 {\n" +
            "    const p = Point{ .x = 10, .y = 20 };\n" +
            "    const k = Kind.b;\n" +
            "    const n = Num{ .i = 12 };\n" +
            "    var nv: c_int = 0;\n" +
            "    switch (n) { .i => |v| { nv = v; }, .f => {} }\n" +
            "    _ = printf(\"p=%d k=%d n=%d\\n\", p.x + p.y, @as(c_int, @intFromEnum(k)), nv);\n" +
            "    return @intCast(p.x);\n" + // 10
            "}\n", 10, "p=30 k=1 n=12" },

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

        // Curated `std.ArrayList(T)` (wall-plan W0) — the modern UNMANAGED array list
        // (zig 0.15+: `.empty`, per-call allocator, `pop()` → ?T). Exercises the type in
        // annotation position, `.empty`, try+append growth across the initial capacity,
        // items subscript/len, pop's optional, a for-over-items sum, appendSlice from
        // `&array`, and defer deinit. `capacity` is deliberately NOT printed: its VALUE is
        // the growth policy's implementation detail (dotcc doubles; zig's curve is
        // super-linear and version-dependent), so it isn't an oracle-comparable observable.
        new object[] { "arraylist",
            "const std = @import(\"std\");\n" +
            "extern fn printf(fmt: [*:0]const u8, ...) c_int;\n" +
            "pub fn main() !void {\n" +
            "    const alloc = std.heap.c_allocator;\n" +
            "    var list: std.ArrayList(i32) = .empty;\n" +
            "    defer list.deinit(alloc);\n" +
            "    var i: i32 = 0;\n" +
            "    while (i < 10) : (i = i + 1) {\n" +
            "        try list.append(alloc, i * 3);\n" +
            "    }\n" +
            "    _ = printf(\"len=%zu first=%d last=%d\\n\", list.items.len, list.items[0], list.items[9]);\n" +
            "    const popped = list.pop();\n" +
            "    if (popped) |v| {\n" +
            "        _ = printf(\"popped=%d len=%zu\\n\", v, list.items.len);\n" +
            "    }\n" +
            "    var sum: i32 = 0;\n" +
            "    for (list.items) |x| {\n" +
            "        sum = sum + x;\n" +
            "    }\n" +
            "    const tail = [2]i32{ 100, 200 };\n" +
            "    try list.appendSlice(alloc, &tail);\n" +
            "    _ = printf(\"sum=%d after=%zu back=%d\\n\", sum, list.items.len, list.items[10]);\n" +
            "}\n", 0,
            "len=10 first=0 last=27\npopped=27 len=9\nsum=108 after=11 back=200" },

        // Type-as-value foundation (wall-plan W1) — Zig's "types are values": a `const` binds a
        // NAME to a TYPE (`const Elem = i32;`), a prefix composes over another alias
        // (`const ElemPtr = *Elem;`), and `@TypeOf(expr)` yields an expression's type both in a
        // type position (`const y: @TypeOf(x) = …;`) and as a `const` alias (`const T = @TypeOf(x);`).
        // An optional over the alias (`?Elem`) rides the value-optional lowering. dotcc emits no
        // runtime decl for an alias — proven end-to-end by matching real zig's stdout byte-for-byte.
        new object[] { "type-value",
            "extern fn printf(fmt: [*:0]const u8, ...) c_int;\n" +
            "const Elem = i32;\n" +
            "fn addElems(a: Elem, b: Elem) Elem { return a + b; }\n" +
            "const ElemPtr = *Elem;\n" +
            "fn bump(p: ElemPtr) void { p.* = p.* + 1; }\n" +
            "pub fn main() void {\n" +
            "    const x: Elem = 20;\n" +
            "    const y: @TypeOf(x) = 22;\n" +
            "    _ = printf(\"sum=%d\\n\", addElems(x, y));\n" +
            "    const T = @TypeOf(x);\n" +
            "    var acc: T = 0;\n" +
            "    acc = acc + x;\n" +
            "    acc = acc + y;\n" +
            "    bump(&acc);\n" +
            "    _ = printf(\"acc=%d\\n\", acc);\n" +
            "    var maybe: ?Elem = null;\n" +
            "    maybe = 7;\n" +
            "    if (maybe) |v| {\n" +
            "        _ = printf(\"maybe=%d\\n\", v);\n" +
            "    }\n" +
            "}\n", 0,
            "sum=42\nacc=43\nmaybe=7" },

        // In-function container declarations (wall-plan W2) — a `const P = struct { … };` inside a
        // body. Exercises a local struct + `.{…}` init + field reads in main, two functions each
        // declaring a same-named-but-differently-shaped local `Rect` (distinct mangled IR types in
        // dotcc, distinct scopes in zig), all fields-only. dotcc registers each on the fly under a
        // function-mangled name; matching real zig's stdout proves the layouts + scoping end-to-end.
        new object[] { "in-fn-struct",
            "extern fn printf(fmt: [*:0]const u8, ...) c_int;\n" +
            "fn area() i32 {\n" +
            "    const Rect = struct { w: i32, h: i32 };\n" +
            "    const r: Rect = .{ .w = 6, .h = 7 };\n" +
            "    return r.w * r.h;\n" +
            "}\n" +
            "fn sum() i32 {\n" +
            "    const Rect = struct { a: i32, b: i32, c: i32 };\n" +
            "    const r: Rect = .{ .a = 10, .b = 20, .c = 30 };\n" +
            "    return r.a + r.b + r.c;\n" +
            "}\n" +
            "pub fn main() void {\n" +
            "    const Point = struct { x: i32, y: i32 };\n" +
            "    const p: Point = .{ .x = 3, .y = 4 };\n" +
            "    _ = printf(\"p=%d,%d area=%d sum=%d\\n\", p.x, p.y, area(), sum());\n" +
            "}\n", 0,
            "p=3,4 area=42 sum=60" },
        // GENERIC FUNCTIONS via comptime VALUE params (wall-plan W3a — call-site monomorphization).
        // addN(10,·)/addN(100,·) → distinct baked-literal instances; powi bakes the loop bound;
        // fib is a RECURSIVE generic — each fib(n-1) folds to a constant arg, transitively
        // instantiating fib__9…fib__0 (the worklist + memoization). addN 15/105, pow 1024/81, fib10=55.
        new object[] { "comptime-param",
            "extern fn printf(fmt: [*:0]const u8, ...) c_int;\n" +
            "fn addN(comptime N: i32, x: i32) i32 { return x + N; }\n" +
            "fn powi(comptime n: u32, x: i64) i64 {\n" +
            "    var r: i64 = 1;\n" +
            "    var i: u32 = 0;\n" +
            "    while (i < n) : (i = i + 1) { r = r * x; }\n" +
            "    return r;\n" +
            "}\n" +
            "fn fib(comptime n: u32) u64 {\n" +
            "    if (n < 2) return n;\n" +
            "    return fib(n - 1) + fib(n - 2);\n" +
            "}\n" +
            "pub fn main() void {\n" +
            "    _ = printf(\"addN10=%d addN100=%d\\n\", addN(10, 5), addN(100, 5));\n" +
            "    _ = printf(\"pow2_10=%lld pow3_4=%lld\\n\", powi(10, 2), powi(4, 3));\n" +
            "    _ = printf(\"fib10=%llu\\n\", fib(10));\n" +
            "}\n", 0,
            "addN10=15 addN100=105\npow2_10=1024 pow3_4=81\nfib10=55" },
        // comptime_optional_fold — a `comptime opt: ?T` value param whose captured `if` FOLDS at
        // instantiation (road-to-zig-std S4b): a payload arg selects the then (binding the capture to the
        // literal), `null` selects the else. The user-generic analog of std.ArrayList's Aligned(T,
        // alignment) Slice selection. choose(41)=42, choose(null)=0 → 42.
        new object[] { "comptime_optional_fold",
            "fn choose(comptime opt: ?u8) u8 { return if (opt) |x| x + 1 else 0; }\n" +
            "pub fn main() u8 { return choose(41) + choose(null); }\n", 42, "" },
        // type_returning_optional_fold — a type-returning generic whose MULTI-statement body computes a
        // type via a captured-`if` fold on a comptime `?T` param (road-to-zig-std S4b pt2 / S4c): the
        // `std.ArrayList` `Aligned(T, alignment)` `const Slice = if (alignment) |a| … else []T;` shape.
        // Store(u8,3) → data:[3]u8 (payload branch, n=3); Store(u8,null) → data:[]u8 (else branch).
        // 20 + 22 = 42.
        new object[] { "type_returning_optional_fold",
            "fn Store(comptime T: type, comptime cap: ?u8) type {\n" +
            "    const Slice = if (cap) |n| [n]T else []T;\n" +
            "    return struct { data: Slice, len: usize };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var arr: Store(u8, 3) = undefined;\n" +
            "    arr.data[0] = 20; arr.data[1] = 0; arr.data[2] = 0; arr.len = 1;\n" +
            "    var buf = [_]u8{ 22, 0 };\n" +
            "    const sl: Store(u8, null) = .{ .data = &buf, .len = 2 };\n" +
            "    return arr.data[0] + sl.data[0];\n" +
            "}\n", 42, "" },

        // GENERIC FUNCTIONS via comptime TYPE params (wall-plan W3b — per-instantiation signatures).
        // maxOf/addOf specialize their parameter+return type per type argument (i32/f64, i64/f32), so
        // each `__i32`/`__f64`/… instance has a DIFFERENT concrete signature; sizeOfType resolves `T`
        // inside the body via @sizeOf. `const I = i32;` proves an alias keys the same resolved instance.
        new object[] { "comptime-type-param",
            "extern fn printf(fmt: [*:0]const u8, ...) c_int;\n" +
            "fn maxOf(comptime T: type, a: T, b: T) T { return if (a > b) a else b; }\n" +
            "fn addOf(comptime T: type, a: T, b: T) T { return a + b; }\n" +
            "fn sizeOfType(comptime T: type) i32 { return @intCast(@sizeOf(T)); }\n" +
            "const I = i32;\n" +
            "pub fn main() void {\n" +
            "    _ = printf(\"max_i32=%d max_f64=%.1f\\n\", maxOf(I, 3, 7), maxOf(f64, 2.5, 1.5));\n" +
            "    _ = printf(\"add_i64=%lld add_f32=%.1f\\n\", addOf(i64, 100, 5), @as(f64, addOf(f32, 1.5, 2.0)));\n" +
            "    _ = printf(\"sz_i32=%d sz_i64=%d sz_f64=%d\\n\", sizeOfType(i32), sizeOfType(i64), sizeOfType(f64));\n" +
            "}\n", 0,
            "max_i32=7 max_f64=2.5\nadd_i64=105 add_f32=3.5\nsz_i32=4 sz_i64=8 sz_f64=8" },

        // TYPE-RETURNING functions (wall-plan W4 — the ArrayList shape). `Pair(T)`/`Node(T)` REIFY a
        // fresh struct per resolved type argument (Pair__i32 / Pair__f64 / Node__i32); a field typed
        // `T` gets the concrete type, `?*const @This()` a self-pointer. `const PairI32 = Pair(i32)` is
        // a top-level alias (resolved once the fn is declared); the head→tail chain derefs the self-ptr.
        new object[] { "type-returning-fn",
            "extern fn printf(fmt: [*:0]const u8, ...) c_int;\n" +
            "fn Pair(comptime T: type) type { return struct { a: T, b: T }; }\n" +
            "fn Node(comptime T: type) type { return struct { value: T, next: ?*const @This() }; }\n" +
            "const PairI32 = Pair(i32);\n" +
            "pub fn main() void {\n" +
            "    const pi: PairI32 = .{ .a = 3, .b = 4 };\n" +
            "    const pf: Pair(f64) = .{ .a = 1.5, .b = 2.5 };\n" +
            "    const tail: Node(i32) = .{ .value = 20, .next = null };\n" +
            "    const head: Node(i32) = .{ .value = 10, .next = &tail };\n" +
            "    _ = printf(\"pi=%d,%d pf=%.1f,%.1f\\n\", pi.a, pi.b, pf.a, pf.b);\n" +
            "    _ = printf(\"head=%d tail=%d\\n\", head.value, head.next.?.value);\n" +
            "}\n", 0,
            "pi=3,4 pf=1.5,2.5\nhead=10 tail=20" },

        // generic-container-methods (road-to-zig-std G4) — a type-returning generic whose reified struct
        // carries METHODS + `const` members, the ArrayList-shaped generic container. Proves: `const Self =
        // @This()` resolves to the REIFIED type in a `*Self` receiver; a receiverless `init()` is reachable
        // through the alias (`S.init()` — the `.empty`/`init` constructor idiom); a sibling method call
        // inside a method body binds to the same mangled instance; a `const` member inlines via `Self.NAME`;
        // and two distinct instantiations (u8 / i32) get INDEPENDENT method sets over their own field types.
        new object[] { "generic-container-methods",
            "extern fn printf(fmt: [*:0]const u8, ...) c_int;\n" +
            "fn Stack(comptime T: type, comptime cap: usize) type {\n" +
            "    return struct {\n" +
            "        items: [cap]T,\n" +
            "        len: usize,\n" +
            "        const Self = @This();\n" +
            "        const CAP = cap;\n" +
            // The idiomatic constructor: a result-located struct literal whose ARRAY field is
            // `undefined`. That member is dropped from the emitted object initializer (a C# `fixed`
            // buffer can't be assigned there — it used to emit CS1666-invalid C# silently).
            "        pub fn init() Self { return .{ .items = undefined, .len = 0 }; }\n" +
            "        pub fn push(self: *Self, v: T) void { self.items[self.len] = v; self.len = self.len + 1; }\n" +
            "        pub fn pop(self: *Self) T { self.len = self.len - 1; return self.items[self.len]; }\n" +
            "        pub fn count(self: *const Self) usize { return self.len; }\n" +
            "        pub fn capacity(self: *const Self) usize { _ = self; return Self.CAP; }\n" +
            "        pub fn sum(self: *const Self) T {\n" +
            "            var total: T = 0;\n" +
            "            var i: usize = 0;\n" +
            "            while (i < self.count()) : (i = i + 1) { total = total + self.items[i]; }\n" +
            "            return total;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "const ByteStack = Stack(u8, 8);\n" +
            "const IntStack = Stack(i32, 4);\n" +
            "pub fn main() void {\n" +
            "    var bs = ByteStack.init();\n" +
            "    bs.push(10); bs.push(32);\n" +
            "    _ = printf(\"u8 n=%llu cap=%llu sum=%d\\n\", bs.count(), bs.capacity(), bs.sum());\n" +
            "    const top = bs.pop();\n" +
            "    _ = printf(\"u8 top=%d left=%llu\\n\", top, bs.count());\n" +
            "    var is = IntStack.init();\n" +
            "    is.push(-5); is.push(105);\n" +
            "    _ = printf(\"i32 n=%llu cap=%llu sum=%d\\n\", is.count(), is.capacity(), is.sum());\n" +
            "    var ds = Stack(u8, 2).init();\n" +
            "    ds.push(1); ds.push(2);\n" +
            "    _ = printf(\"direct n=%llu cap=%llu sum=%d\\n\", ds.count(), ds.capacity(), ds.sum());\n" +
            "}\n", 0,
            "u8 n=2 cap=8 sum=42\nu8 top=32 left=1\ni32 n=2 cap=4 sum=100\ndirect n=2 cap=2 sum=3" },

        // ANYTYPE parameters (wall-plan W5 — the monomorphization capstone). An `anytype` param's type
        // is INFERRED from the argument (`@TypeOf(arg)`), then keys a specialization like a comptime TYPE
        // param — but the arg is ALSO passed at runtime. add/maxOf specialize per inferred pair (i32/f64),
        // the `@TypeOf(a)` return following suit; getX/firstLen show duck-typed member access (`.x` on a
        // struct, `.len` on a slice) lowering against the inferred concrete type.
        new object[] { "anytype-param",
            "extern fn printf(fmt: [*:0]const u8, ...) c_int;\n" +
            "const Point = struct { x: i32, y: i32 };\n" +
            "fn add(a: anytype, b: anytype) @TypeOf(a) { return a + b; }\n" +
            "fn maxOf(a: anytype, b: anytype) @TypeOf(a) { return if (a > b) a else b; }\n" +
            "fn getX(p: anytype) i32 { return p.x; }\n" +
            "fn firstLen(s: anytype) usize { return s.len; }\n" +
            "pub fn main() void {\n" +
            "    _ = printf(\"add_i=%d add_f=%.1f\\n\", add(@as(i32, 3), @as(i32, 4)), add(@as(f64, 1.5), @as(f64, 2.5)));\n" +
            "    _ = printf(\"max_i=%d max_f=%.1f\\n\", maxOf(@as(i32, 7), @as(i32, 2)), maxOf(@as(f64, 1.5), @as(f64, 4.5)));\n" +
            "    const p = Point{ .x = 42, .y = 7 };\n" +
            "    const arr = [_]i32{ 1, 2, 3, 4 };\n" +
            "    _ = printf(\"x=%d len=%d\\n\", getX(p), @as(i32, @intCast(firstLen(arr[0..]))));\n" +
            "}\n", 0,
            "add_i=7 add_f=4.0\nmax_i=7 max_f=4.5\nx=42 len=4" },

        // std.debug.print (wall-plan W6 — the biggest remaining std idiom, last brick of the arc). The
        // comptime format is parsed at lowering time and its {…} placeholders paired positionally with
        // the tuple; it lowers to fprintf(stderr, …) — so (like real Zig) the output is on STDERR, which
        // the harness now captures and folds into the compared output. {d}/{s}/{c}/{x}/{X}, {{ }} braces,
        // and a literal % are all exercised; {d} on an i64 prints the full 64-bit value.
        new object[] { "debug-print",
            "const std = @import(\"std\");\n" +
            "const Point = struct { x: i32, y: i32 };\n" +
            "pub fn main() void {\n" +
            "    const n: i32 = 42;\n" +
            "    const big: i64 = 5000000000;\n" +
            "    const p = Point{ .x = 3, .y = 7 };\n" +
            "    std.debug.print(\"hello {s}! n={d} big={d}\\n\", .{ \"world\", n, big });\n" +
            "    std.debug.print(\"hex={x} up={X} char={c}\\n\", .{ 255, 255, 65 });\n" +
            "    std.debug.print(\"point {{x={d}, y={d}}} pct=100%\\n\", .{ p.x, p.y });\n" +
            "}\n", 0,
            "hello world! n=42 big=5000000000\nhex=ff up=FF char=A\npoint {x=3, y=7} pct=100%" },
        // @typeInfo — comptime reflection folded at lowering time (road-to-zig-std S5). The headline
        // shape: `switch (@typeInfo(T))` dispatches on the kind, once per instantiation. 1+2+3+4+5+6+0
        // = 21, doubled = 42.
        new object[] { "typeinfo_kind",
            "fn kindOf(comptime T: type) u8 {\n" +
            "    return switch (@typeInfo(T)) {\n" +
            "        .int => 1,\n" +
            "        .float => 2,\n" +
            "        .bool => 3,\n" +
            "        .pointer => 4,\n" +
            "        .optional => 5,\n" +
            "        .array => 6,\n" +
            "        else => 0,\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const sum = kindOf(u8) + kindOf(f64) + kindOf(bool) + kindOf(*u8) + kindOf(?u8) + kindOf([3]u8) + kindOf(void);\n" +
            "    return sum * 2;\n" +
            "}\n", 42, "" },
        // A prong CAPTURE binding the payload, plus `signedness` — which is exactly recoverable from
        // the lowered type, so it is answered rather than cut. 1*40 + 0 + 9*2 - 16 = 42.
        new object[] { "typeinfo_signedness",
            "fn signBit(comptime T: type) u8 {\n" +
            "    return switch (@typeInfo(T)) {\n" +
            "        .int => |i| if (i.signedness == .signed) 1 else 0,\n" +
            "        else => 9,\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 { return signBit(i32) * 40 + signBit(u32) + signBit(f32) * 2 - 16; }\n", 42, "" },
        // The declared-width fidelity rule: dotcc widens `u21` to a 32-bit `uint`, so `bits` is read
        // off the SOURCE spelling (the rule @typeName follows) and agrees with zig — 21, not 32. This
        // case is the one that would silently diverge if the lowered width were reported instead.
        new object[] { "typeinfo_bits",
            "pub fn main() u8 {\n" +
            "    const a: u16 = @typeInfo(u21).int.bits;\n" +
            "    const b: u16 = @typeInfo(i7).int.bits;\n" +
            "    const c: u16 = @typeInfo(f64).float.bits;\n" +
            "    return @intCast(a + b + c - 50);\n" +
            "}\n", 42, "" },
        // `.child` is a TYPE (folded in a type position) and `.len` a comptime integer. 36 + 2 + 4 = 42.
        new object[] { "typeinfo_child",
            "pub fn main() u8 {\n" +
            "    const C = @typeInfo(?u32).optional.child;\n" +
            "    const A = @typeInfo([4]u8).array.child;\n" +
            "    const n: C = 36;\n" +
            "    const m: A = 2;\n" +
            "    const len: u8 = @typeInfo([4]u8).array.len;\n" +
            "    return @intCast(n + m + len);\n" +
            "}\n", 42, "" },
        // The declared width now rides a comptime `type` param and an alias. This program is the
        // regression guard for the instance KEY: `u21` and `u32` lower to the same `uint`, so before
        // the width joined the mangle they shared one memoized instance and one `bits` answer — this
        // would have returned 21+21 or 32+32, not 21+32. 21+32+21+7-39 = 42.
        new object[] { "typeinfo_bits_generic",
            "fn bitsOf(comptime T: type) u16 { return @typeInfo(T).int.bits; }\n" +
            "const Cp = u21;\n" +
            "pub fn main() u8 {\n" +
            "    const a = bitsOf(u21);\n" +
            "    const b = bitsOf(u32);\n" +
            "    const c = bitsOf(Cp);\n" +
            "    const d = bitsOf(i7);\n" +
            "    return @intCast(a + b + c + d - 39);\n" +
            "}\n", 42, "" },
        // The width is shadow-saved and restored in lockstep with the type binding: BOTH params are
        // named `T`, so a missed restore would give `outer` the inner instance's 7. 7 + 21 + 14 = 42.
        new object[] { "typeinfo_bits_nested_shadow",
            "fn inner(comptime T: type) u16 { return @typeInfo(T).int.bits; }\n" +
            "fn outer(comptime T: type) u16 { return inner(u7) + @typeInfo(T).int.bits; }\n" +
            "pub fn main() u8 { return @intCast(outer(u21) + 14); }\n", 42, "" },
        // `inline for` over a COMPTIME LIST (road-to-zig-std S6), in the three shapes that are
        // version-stable: a `[_]type{…}` literal, two lists walked in PARALLEL, and a list with an
        // index capture. None of them mentions `@typeInfo`, so — unlike the member-list forms below —
        // this program is valid under 0.16.0 as well and gets a real differential. 15 + 9 + 18 = 42.
        new object[] { "inline_for_comptime_lists",
            "pub fn main() u8 {\n" +
            "    var total: usize = 0;\n" +
            "    inline for ([_]type{ u8, u16, u32, u64 }) |T| {\n" +
            "        total += @sizeOf(T);\n" +
            "    }\n" +
            "    inline for ([_]type{ u8, u16 }, [_]type{ u32, u64 }) |A, B| {\n" +
            "        total += @sizeOf(B) - @sizeOf(A);\n" +
            "    }\n" +
            "    inline for ([_]type{ u8, u16, u64 }, 0..) |T, i| {\n" +
            "        total += @sizeOf(T) * i;\n" +
            "    }\n" +
            "    return @intCast(total);\n" +
            "}\n", 42, "" },
        // NOTE: there is deliberately NO member-list oracle program here. dotcc's front end targets
        // zig 0.17-dev (the campaign compiles real std from 0.17.0-dev.667, and the grammar tracks it),
        // where `@typeInfo(T).@"struct"` exposes the PARALLEL ARRAYS `field_names` / `field_types` /
        // `field_values`. The CI oracle pins the newest DURABLE tagged release, 0.16.0 (dev tarballs are
        // GC'd off the download index within days — see the zig-oracle job comment), and 0.16.0 still has
        // the older `fields: []const StructField` shape. So a member-list program cannot be valid in both
        // compilers at once: it is covered by emit pins (ZigFrontendTests) plus a by-hand run against the
        // 0.17-dev install, and gains a differential here the moment 0.17.0 is tagged. The membership
        // builtins below ARE version-stable, so they get a real differential — as does the S6
        // `inline_for_comptime_lists` program above, which is why it iterates `[_]type{…}` literals
        // rather than member lists: the UNROLL is the same code path either way, so exercising it over
        // a version-stable operand still puts it under a real compiler.
        // Membership + comptime-named field access. `@hasDecl` sees both a container `const` and a
        // METHOD. The absent names take their `else` arms — their weighted true-arms (10, 20) would
        // blow the total, so a constant-true fold could not reach 42. 1+0+4+8+0+29 = 42.
        new object[] { "typeinfo_membership",
            "const P = struct {\n" +
            "    x: i32,\n" +
            "    y: i32,\n" +
            "    const K: i32 = 7;\n" +
            "    fn get(self: P) i32 { return self.x; }\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    const a: u8 = if (@hasField(P, \"x\")) 1 else 0;\n" +
            "    const b: u8 = if (@hasField(P, \"q\")) 10 else 0;\n" +
            "    const c: u8 = if (@hasDecl(P, \"get\")) 4 else 0;\n" +
            "    const d: u8 = if (@hasDecl(P, \"K\")) 8 else 0;\n" +
            "    const e: u8 = if (@hasDecl(P, \"nope\")) 20 else 0;\n" +
            "    var p = P{ .x = 27, .y = 2 };\n" +
            "    p.x += 0;\n" +
            "    const fx: u8 = @intCast(@field(p, \"x\") + @field(p, \"y\"));\n" +
            "    return a + b + c + d + e + fx;\n" +
            "}\n", 42, "" },
        // The REIFICATION builtins (road-to-zig-std S7). Unlike the member lists above, `@Int` and
        // `@bitSizeOf` ARE version-stable — 0.16.0 already has the `@Type`-to-`@Int`/`@Struct`/`@Enum`
        // split, and `@bitSizeOf` is ancient — so this brick gets a real differential on both halves.
        // A constructed type has to hold values as well as report a width, hence the two round-trip
        // terms that contribute 0. 21 + 9 + 1 + 16 - 5 = 42.
        new object[] { "reify_int_and_bitsizeof",
            "const E = enum(u16) { a, b };\n" +
            "fn widthOf(comptime T: type) i32 { return @bitSizeOf(T); }\n" +
            "pub fn main() u8 {\n" +
            "    const Wide = @Int(.unsigned, 21);\n" +
            "    const Narrow = @Int(.signed, 9);\n" +
            "    var x: Wide = 1000;\n" +
            "    var y: Narrow = -5;\n" +
            "    x += 0;\n" +
            "    y += 0;\n" +
            "    var total: i32 = 0;\n" +
            "    total += @bitSizeOf(Wide);\n" +
            "    total += widthOf(Narrow);\n" +
            "    total += @bitSizeOf(bool);\n" +
            "    total += @bitSizeOf(E);\n" +
            "    total -= @bitSizeOf(u5);\n" +
            "    total += @as(i32, x) - 1000;\n" +
            "    total += @as(i32, y) + 5;\n" +
            "    return @intCast(total);\n" +
            "}\n", 42, "" },
        // `@compileError` where it is NOT reached — the half that matters, since 231 of its 595 uses in
        // the pinned std are an `else =>` guard and the rest are comptime `if` guards. Every diagnostic
        // here would fire if the folds failed, so the program compiling AT ALL under both compilers is
        // the assertion; a real zig that analysed any of them would fail the build outright. The
        // top-level `REMOVED` is the deprecation-tombstone shape: inert until something names it.
        // 21 + 21 = 42.
        new object[] { "compile_error_guards_fold_away",
            "pub const REMOVED = @compileError(\"use something else\");\n" +
            "fn onlyInts(comptime T: type) i32 {\n" +
            "    return switch (@typeInfo(T)) {\n" +
            "        .int => @bitSizeOf(T),\n" +
            "        else => @compileError(\"onlyInts wants an integer, got \" ++ @typeName(T)),\n" +
            "    };\n" +
            "}\n" +
            "fn narrow(comptime T: type) i32 {\n" +
            "    if (@bitSizeOf(T) > 64) @compileError(\"too wide for narrow()\");\n" +
            "    return @bitSizeOf(T);\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    @setEvalBranchQuota(10000);\n" +
            "    return @intCast(onlyInts(u21) + narrow(u21));\n" +
            "}\n", 42, "" },
        // The synthetic `builtin` module (road-to-zig-std S3). Every branch here is decided at
        // COMPILE time by the target description, so agreeing with real zig means dotcc's synthetic
        // module describes the same host real zig does. Deliberately does NOT read `builtin.mode`:
        // dotcc reports `.ReleaseFast` on purpose (it does not trap integer overflow) while
        // `zig build-exe` defaults to `.Debug` - a divergence by design, and the one member that
        // could not be differentially tested without passing `-OReleaseFast`. 1+2+4+8+16+32 = 63.
        new object[] { "builtin_target_queries",
            "const builtin = @import(\"builtin\");\n" +
            "pub fn main() u8 {\n" +
            "    var n: u32 = 0;\n" +
            "    if (builtin.link_libc) { n += 1; } else { n += 100; }\n" +
            "    if (builtin.single_threaded) { n += 200; } else { n += 2; }\n" +
            "    if (builtin.os.tag == .plan9) { n += 400; } else { n += 4; }\n" +
            "    if (builtin.cpu.arch == .avr) { n += 800; } else { n += 8; }\n" +
            "    if (builtin.target.abi == .gnu or builtin.target.abi == .none) { n += 16; } else { n += 1600; }\n" +
            "    if (builtin.is_test) { n += 3200; } else { n += 32; }\n" +
            "    return @intCast(n - 21);\n" +
            "}\n", 42, "" },
        // The W4 lift (road-to-zig-std G4 blocker 2): a type-returning body EVALUATED at comptime.
        // `List` DELEGATES (`std.ArrayList`'s own `return array_list.Aligned(T, null);`), `Aligned` opens
        // with a comptime `if` that returns early for a known alignment, `Child` folds a `switch` over
        // `@typeInfo`, `Fit`/`Wider` fold an `if` on a value / a type comparison, and `U` returns `@Int`.
        // The load-bearing line is `const b: Aligned(u8, 1) = a;` — zig accepts it only because
        // `Aligned(u8, 1)`, `Aligned(u8, null)` and `List(u8)` are ONE type, which is exactly what the
        // delegation memo has to reproduce. Version-stable (0.16.0's `@Int` split already landed).
        // 10 + 5 + 7 + 10 + 10 + 0 = 42.
        new object[] { "type_body_delegation",
            "fn Aligned(comptime T: type, comptime alignment: ?u8) type {\n" +
            "    if (alignment) |a| {\n" +
            "        if (a == 1) return Aligned(T, null);\n" +
            "    }\n" +
            "    return struct { v: T };\n" +
            "}\n" +
            "fn List(comptime T: type) type {\n" +
            "    return Aligned(T, null);\n" +
            "}\n" +
            "fn Child(comptime T: type) type {\n" +
            "    return switch (@typeInfo(T)) {\n" +
            "        .pointer => |info| info.child,\n" +
            "        .optional => |info| info.child,\n" +
            "        else => @compileError(\"expected a pointer or an optional\"),\n" +
            "    };\n" +
            "}\n" +
            "fn Fit(comptime n: u16) type {\n" +
            "    return if (n > 255) u16 else u8;\n" +
            "}\n" +
            "fn Wider(comptime T: type) type {\n" +
            "    return if (T == u8) u16 else T;\n" +
            "}\n" +
            "fn U(comptime n: u16) type {\n" +
            "    return @Int(.unsigned, n);\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const a: List(u8) = .{ .v = 10 };\n" +
            "    const b: Aligned(u8, 1) = a;\n" +
            "    const c: Child(*u16) = 5;\n" +
            "    const d: Fit(300) = 7;\n" +
            "    const e: Wider(u8) = 300;\n" +
            "    var total: u32 = b.v;\n" +
            "    total += c;\n" +
            "    total += d;\n" +
            "    total += e - 290;\n" +
            "    total += @bitSizeOf(U(21)) - 11;\n" +
            "    total += @bitSizeOf(Fit(200)) - 8;\n" +
            "    return @intCast(total);\n" +
            "}\n", 42, "" },
        // NESTED containers as full containers — the wall `std.fmt.bufPrint` hit first: `std.fmt.Number`
        // has a field `mode: Mode = .decimal` typed by a nested `pub const Mode = enum {…}` declared AFTER
        // it, carrying a method. Also a grandchild naming an uncle plainly (`k: K` inside `A.B`), a
        // three-segment qualified type (`A.B.C`), a qualified enum member (`Number.Mode.decimal`) and a
        // qualified const + static call (`A.P.EXTRA`, `A.P.two()`). Declarations never sit BETWEEN fields —
        // zig rejects that, and dotcc is lenient about it. 16 + 10 + 10 + 4 + 2 = 42.
        new object[] { "nested_containers",
            "const Number = struct {\n" +
            "    mode: Mode = .decimal,\n" +
            "    width: ?usize = null,\n" +
            "\n" +
            "    pub const Mode = enum {\n" +
            "        decimal,\n" +
            "        hex,\n" +
            "\n" +
            "        pub fn base(mode: Mode) u8 {\n" +
            "            return switch (mode) {\n" +
            "                .decimal => 10,\n" +
            "                .hex => 16,\n" +
            "            };\n" +
            "        }\n" +
            "    };\n" +
            "};\n" +
            "const A = struct {\n" +
            "    b: B,\n" +
            "\n" +
            "    pub const K = enum { x, y };\n" +
            "    pub const B = struct {\n" +
            "        k: K,\n" +
            "        c: C,\n" +
            "\n" +
            "        pub const C = struct { v: u8 };\n" +
            "    };\n" +
            "    pub const P = struct {\n" +
            "        pub const EXTRA: u8 = 4;\n" +
            "        pub fn two() u8 {\n" +
            "            return 2;\n" +
            "        }\n" +
            "    };\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    const n: Number = .{ .mode = .hex };\n" +
            "    const d: Number.Mode = Number.Mode.decimal;\n" +
            "    const a: A = .{ .b = .{ .k = .y, .c = .{ .v = 10 } } };\n" +
            "    const c: A.B.C = a.b.c;\n" +
            "    var total: u8 = n.mode.base();\n" +
            "    total += d.base();\n" +
            "    total += c.v;\n" +
            "    if (a.b.k == .y) total += A.P.EXTRA;\n" +
            "    total += A.P.two();\n" +
            "    return total;\n" +
            "}\n", 42, "" },
        // STATEMENT-shaped `catch`/`orelse` fallbacks (road-to-zig-std — ~1,000 std sites, each a parse
        // error that dropped its whole function, `std.fmt.bufPrint` among them): `catch |err| switch
        // (err)` whose prongs yield or jump, `catch { return …; }` / `orelse { return …; }`, `catch |e|
        // return e` (an ERROR return of the runtime code — it used to be wrapped as the success payload),
        // `orelse continue` / `orelse break`, a labeled `break :blk v`, and a discarded `catch {}`.
        // 100 + 50 + 7 + 3 + 9 + 2 + 29 = 200; 200 - 158 = 42.
        new object[] { "fallback_arms",
            "const E = error{ Bad, Worse };\n" +
            "\n" +
            "fn parse(x: u8) E!u8 {\n" +
            "    if (x == 0) return error.Bad;\n" +
            "    if (x == 1) return error.Worse;\n" +
            "    return x;\n" +
            "}\n" +
            "\n" +
            "fn lookup(x: u8) ?u8 {\n" +
            "    return if (x > 5) x else null;\n" +
            "}\n" +
            "\n" +
            "// `catch |err| switch (err)` — the 498-site shape: prongs yield a value or jump.\n" +
            "fn classify(x: u8) E!u8 {\n" +
            "    const v = parse(x) catch |err| switch (err) {\n" +
            "        error.Bad => 100,\n" +
            "        error.Worse => return error.Worse,\n" +
            "    };\n" +
            "    return v;\n" +
            "}\n" +
            "\n" +
            "// `catch { … }` / `orelse { … }` blocks that never fall through.\n" +
            "fn orDefault(x: u8) u8 {\n" +
            "    const v = parse(x) catch {\n" +
            "        return 7;\n" +
            "    };\n" +
            "    const w = lookup(v) orelse {\n" +
            "        return 3;\n" +
            "    };\n" +
            "    return w;\n" +
            "}\n" +
            "\n" +
            "// `catch |e| return e` — capture + return.\n" +
            "fn passthrough(x: u8) E!u8 {\n" +
            "    const v = parse(x) catch |e| return e;\n" +
            "    return v + 1;\n" +
            "}\n" +
            "\n" +
            "// `orelse break` / `orelse continue` / labeled break with a value.\n" +
            "fn loops() u32 {\n" +
            "    var total: u32 = 0;\n" +
            "    var i: u8 = 0;\n" +
            "    while (i < 10) : (i += 1) {\n" +
            "        const v = lookup(i) orelse continue;\n" +
            "        if (v == 9) {\n" +
            "            const w = lookup(0) orelse break;\n" +
            "            total += w;\n" +
            "        }\n" +
            "        total += v;\n" +
            "    }\n" +
            "    const found = blk: {\n" +
            "        const x = lookup(8) orelse break :blk 0;\n" +
            "        break :blk x;\n" +
            "    };\n" +
            "    return total + found;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var total: u32 = 0;\n" +
            "    total += classify(0) catch 0; // 100\n" +
            "    total += classify(1) catch 50; // 50\n" +
            "    total += orDefault(0); // 7\n" +
            "    total += orDefault(2); // 3\n" +
            "    total += orDefault(9); // 9\n" +
            "    total += passthrough(1) catch 2; // 2\n" +
            "    // statement-position `catch {}` on a discarded result\n" +
            "    _ = parse(0) catch {};\n" +
            "    total += loops(); // 6+7+8 = 21, +8 = 29\n" +
            "    return @intCast(total - 158); // 200 - 158 = 42\n" +
            "}\n", 42, "" },
        // The `else => |e| return e` capture prong that ends most `catch |err| switch (err)` blocks in
        // std: the capture binds the error, the prong returns it, a matched prong yields a value.
        // 30 + 5 + 7 = 42.
        new object[] { "fallback_switch_capture_prong",
            "const E = error{ Bad, Worse, Worst };\n" +
            "fn parse(x: u8) E!u8 {\n" +
            "    if (x == 0) return error.Bad;\n" +
            "    if (x == 1) return error.Worse;\n" +
            "    if (x == 2) return error.Worst;\n" +
            "    return x;\n" +
            "}\n" +
            "fn soften(x: u8) E!u8 {\n" +
            "    return parse(x) catch |err| switch (err) {\n" +
            "        error.Bad => 30,\n" +
            "        else => |e| return e,\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var total: u8 = 0;\n" +
            "    total += soften(0) catch 0; // 30\n" +
            "    total += soften(1) catch 5; // 5 (Worse propagated)\n" +
            "    total += soften(7) catch 0; // 7\n" +
            "    return total;\n" +
            "}\n", 42, "" },
        // A string literal's `.len` excludes its NUL sentinel (zig types it `*const [N:0]u8`) — directly,
        // through a local or a global `const` bound to one, and through the slice coercion of such a
        // binding; dotcc's lowered `char[N+1]` counted the NUL in `.len` (a found miscompile). Plus a named
        // integer `const` as a comptime argument, which folds like a literal. 3+5+4+3+5+22 = 42.
        new object[] { "string_literal_len",
            "const G = \"hello\";\n" +
            "const N: u8 = 20;\n" +
            "fn sliceLen(s: []const u8) usize {\n" +
            "    return s.len;\n" +
            "}\n" +
            "fn addN(comptime n: u8, x: u8) u8 {\n" +
            "    return x + n;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const s = \"abc\";\n" +
            "    var total: usize = 0;\n" +
            "    total += s.len;\n" +
            "    total += G.len;\n" +
            "    total += \"wxyz\".len;\n" +
            "    total += sliceLen(s);\n" +
            "    total += sliceLen(G);\n" +
            "    total += addN(N, 2);\n" +
            "    return @intCast(total);\n" +
            "}\n", 42, "" },
        // DECL LITERALS (zig 0.14+; road-to-zig-std G3 — bufPrint's `var w: Writer = .fixed(buf);`): a
        // `.name(args)` / `.name()` call and a bare `.name` value at a struct result location name the
        // RESULT type's own declaration — at a typed `var`/`const`, a parameter, and a `return`.
        // 10 + 0 (take) + 2 + 0 + 30 = 42.
        new object[] { "decl_literals",
            "const Counter = struct {\n" +
            "    n: u8,\n" +
            "    pub const zero: Counter = .{ .n = 0 };\n" +
            "    pub fn init(n: u8) Counter {\n" +
            "        return .{ .n = n };\n" +
            "    }\n" +
            "    pub fn fixed() Counter {\n" +
            "        return .{ .n = 2 };\n" +
            "    }\n" +
            "};\n" +
            "fn make() Counter {\n" +
            "    return .init(30);\n" +
            "}\n" +
            "fn take(c: Counter) u8 {\n" +
            "    return c.n;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var a: Counter = .init(10);\n" +
            "    const b: Counter = .fixed();\n" +
            "    const z: Counter = .zero;\n" +
            "    a.n += take(.init(0));\n" +
            "    return a.n + b.n + z.n + make().n;\n" +
            "}\n", 42, "" },
        // A `comptime if` statement and an anonymous `enum { … }` parameter type (road-to-zig-std S9, the
        // parse gaps behind std's findScalarPos and parseIntWithSign). The block arms run at comptime,
        // assigning the `comptime var`. 20 + 2 + 20 + 0 = 42.
        new object[] { "comptime_if_anon_enum",
            "fn weight(comptime T: type) u8 {\n" +
            "    comptime var w: u8 = 1;\n" +
            "    comptime if (@sizeOf(T) > 1) {\n" +
            "        w = 20;\n" +
            "    } else {\n" +
            "        w = 2;\n" +
            "    };\n" +
            "    return w;\n" +
            "}\n" +
            "fn sign(comptime s: enum { pos, neg }, x: u8) u8 {\n" +
            "    return switch (s) {\n" +
            "        .pos => x,\n" +
            "        .neg => 0,\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return weight(u32) + weight(u8) + sign(.pos, 20) + sign(.neg, 7);\n" +
            "}\n", 42, "" },
        // zig's VOID value `{}` and void-typed storage (std.sort's `context: void`): a void parameter
        // (erased from the C# signature, the calls and the fn-pointer type), `{}` through an `anytype`,
        // a void local and a store into it, `return {};`. 20 + 2 + 20 = 42.
        new object[] { "void_value",
            "var hits: u8 = 0;\n" +
            "fn bump() void {\n" +
            "    hits += 20;\n" +
            "    return {};\n" +
            "}\n" +
            "fn lessThan(context: void, a: u8, b: u8) bool {\n" +
            "    _ = context;\n" +
            "    return a < b;\n" +
            "}\n" +
            "fn larger(context: anytype, a: u8, b: u8, less: *const fn (@TypeOf(context), u8, u8) bool) u8 {\n" +
            "    return if (less(context, a, b)) b else a;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var unit: void = {};\n" +
            "    unit = {};\n" +
            "    bump();\n" +
            "    const v = {};\n" +
            "    return hits + larger(v, 2, 1, &lessThan) + larger(unit, 0, 20, &lessThan);\n" +
            "}\n", 42, "" },
        // The parse gaps that kept std.array_list.Aligned from parsing (road-to-zig-std S9/G4): `inline fn`
        // (top level and member), a general sentinel in a type and a slicing expression (zig's Debug mode
        // checks `buf[2] == 3`), `align(E)` pointer / slice types, a trailing comma in a call.
        // (40 + 2) + 3 - 3 = 42.
        new object[] { "std_parse_bricks",
            "const Box = struct {\n" +
            "    v: u8,\n" +
            "    pub inline fn get(self: Box) u8 {\n" +
            "        return self.v;\n" +
            "    }\n" +
            "};\n" +
            "inline fn add(a: u8, b: u8) u8 {\n" +
            "    return a + b;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const buf = [_]u8{ 40, 2, 3 };\n" +
            "    const s: [:3]const u8 = buf[0..2 :3];\n" +
            "    const p: *align(1) const u8 = &buf[1];\n" +
            "    const all: []align(1) const u8 = &buf;\n" +
            "    const b = Box{ .v = add(\n" +
            "        s[0],\n" +
            "        p.*,\n" +
            "    ) };\n" +
            "    return b.get() + @as(u8, @intCast(all.len)) - 3;\n" +
            "}\n", 42, "" },
        // More of hash_map's syntax (road-to-zig-std S9/G5): a member `comptime { … }` block, a BLOCK
        // continue expression `while (…) : ({ … })`, and `orelse return <identifier>`. 30 + 6 + 6 = 42.
        new object[] { "hash_map_parse_bricks",
            "const S = struct {\n" +
            "    a: u8,\n" +
            "    comptime {\n" +
            "        const z = 1;\n" +
            "        _ = z;\n" +
            "    }\n" +
            "};\n" +
            "fn pick(x: ?u8, fallback: u8) u8 {\n" +
            "    const v = x orelse return fallback;\n" +
            "    return v;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var i: u8 = 0;\n" +
            "    var total: u8 = 0;\n" +
            "    while (i < 3) : ({\n" +
            "        i += 1;\n" +
            "        total += 2;\n" +
            "    }) {}\n" +
            "    const s = S{ .a = 30 };\n" +
            "    return s.a + total + pick(null, 6);\n" +
            "}\n", 42, "" },
        // A `switch` STATEMENT closed by `;` as the body of an `if` / capture `if` (zig's expression
        // statement; fmt.zig's parseIntWithSign). classify('b') = 30 + 10 + 2, classify(null) = 2.
        new object[] { "switch_statement_semicolon",
            "fn classify(x: ?u8) u8 {\n" +
            "    var r: u8 = 0;\n" +
            "    if (x) |c| switch (c) {\n" +
            "        'b' => {\n" +
            "            r = 30;\n" +
            "        },\n" +
            "        'x' => {\n" +
            "            r = 1;\n" +
            "        },\n" +
            "        else => {},\n" +
            "    };\n" +
            "    if (r > 0) switch (r) {\n" +
            "        30 => {\n" +
            "            r += 10;\n" +
            "        },\n" +
            "        else => {},\n" +
            "    };\n" +
            "    return r + 2;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return classify('b') + classify(null) - 2;\n" +
            "}\n", 42, "" },
        // TYPE const members of a reified struct (road-to-zig-std G4/G5; Aligned's `Slice`, HashMap's
        // `Unmanaged`): declared after the fields they type, evaluated with the instantiation's seeds.
        // 30 + 1 + 1 + 6 + 2 + 2 = 42.
        new object[] { "reified_type_consts",
            "fn Inner(comptime K: type, comptime V: type) type {\n" +
            "    return struct { k: K, v: V };\n" +
            "}\n" +
            "fn Outer(comptime K: type, comptime cap: ?usize) type {\n" +
            "    return struct {\n" +
            "        pair: Pair,\n" +
            "        items: Items,\n" +
            "        pub const Pair = Inner(K, u8);\n" +
            "        pub const Items = if (cap) |_| []const K else []const u8;\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const A = Outer(u16, 2);\n" +
            "    const wide = [_]u16{ 1, 1 };\n" +
            "    const a: A = .{ .pair = .{ .k = 30, .v = 6 }, .items = &wide };\n" +
            "    const narrow = [_]u8{ 2, 0 };\n" +
            "    const b: Outer(u16, null) = .{ .pair = .{ .k = 0, .v = 0 }, .items = &narrow };\n" +
            "    return @as(u8, @intCast(a.pair.k + a.items[0] + a.items[1])) + a.pair.v + b.items[0] + 2;\n" +
            "}\n", 42, "" },
        new object[] { "reified_value_seeds",
            "fn Buf(comptime cap: ?u8, comptime n: u8) type {\n" +
            "    return struct {\n" +
            "        len: u8 = n,\n" +
            "        pub const max = if (cap) |c| c else 0;\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const A = Buf(30, 2);\n" +
            "    const b: A = .{};\n" +
            "    const B = Buf(null, 10);\n" +
            "    const c: B = .{};\n" +
            "    return A.max + b.len + B.max + c.len;\n" +
            "}\n", 42, "" },
        // The comptime-call engine V1: std's own maxInt / minInt source, so the program is self-contained
        // for the CI oracle (the real-std leg is Dotcc_matches_zig_std_math_max_min_int_from_source).
        new object[] { "comptime_int_calls",
            "fn maxInt(comptime T: type) comptime_int {\n" +
            "    const info = @typeInfo(T).int;\n" +
            "    return (1 << (info.bits - @intFromBool(info.signedness == .signed))) - 1;\n" +
            "}\n" +
            "fn minInt(comptime T: type) comptime_int {\n" +
            "    const info = @typeInfo(T).int;\n" +
            "    return switch (info.signedness) {\n" +
            "        .unsigned => 0,\n" +
            "        .signed => -(1 << (info.bits - 1)),\n" +
            "    };\n" +
            "}\n" +
            "fn span(comptime T: type) comptime_int {\n" +
            "    return maxInt(T) - minInt(T);\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const a: u8 = maxInt(u8);\n" +
            "    const b: i16 = minInt(i16);\n" +
            "    var r: u8 = 0;\n" +
            "    if (a == 255) r += 10;\n" +
            "    if (b == -32768) r += 10;\n" +
            "    if (span(i8) == 255) r += 10;\n" +
            "    if (maxInt(u64) == 18446744073709551615) r += 12;\n" +
            "    return r;\n" +
            "}\n", 42, "" },
        // A RUNTIME multi-object `for` (road-to-zig-std G5; hash_map's `for (metadata, keys, values)
        // |m, k, v|`): three objects with a trailing comma (an array pointer, an array, a slice) and a
        // `continue` / `break`; a pair whose body breaks out of a switch; a pair with a `_` capture; and an
        // ASSIGNMENT prong body (`.stage2_llvm => _ = &dbHelper,`). 44 + 0 - 30 + 27 + 1 = 42.
        new object[] { "multi_object_for",
            "pub fn main() u8 {\n" +
            "    const used = [_]bool{ true, false, true, true };\n" +
            "    const keys = [_]u8{ 1, 2, 3, 4 };\n" +
            "    var vals = [_]u8{ 10, 20, 30, 40 };\n" +
            "    var total: u8 = 0;\n" +
            "    for (\n" +
            "        &used,\n" +
            "        &keys,\n" +
            "        vals[0..],\n" +
            "    ) |m, k, v| {\n" +
            "        if (!m) continue;\n" +
            "        total += k + v;\n" +
            "        if (k == 3) break;\n" +
            "    }\n" +
            "    var pairs: u8 = 0;\n" +
            "    for (keys, vals) |k, v| {\n" +
            "        switch (k) {\n" +
            "            4 => break,\n" +
            "            else => {},\n" +
            "        }\n" +
            "        pairs += v - k * 10;\n" +
            "    }\n" +
            "    var n: u8 = 0;\n" +
            "    for (keys[0..2], vals[0..2],) |_, v| n += v;\n" +
            "    var bonus: u8 = 0;\n" +
            "    switch (n) {\n" +
            "        30 => bonus = 1,\n" +
            "        else => _ = &bonus,\n" +
            "    }\n" +
            "    return total + pairs - n + 27 + bonus;\n" +
            "}\n", 42, "" },
        // The CLOSURE idiom (std.sort.asc's `return struct { pub fn inner … }.inner;`, road-to-zig-std G3)
        // and COMPTIME function parameters (`comptime lessThan: fn (@TypeOf(context), T, T) bool`, what
        // std.mem.sort takes): a function value keys each instance and is called directly, also passed along
        // to another generic; the idiom in expression position; `noalias`; a by-reference pair capture; and a
        // `comptime { … }` prong body. 40 + 2 + 23 - 22 + 0 + 1 - 2 = 42.
        new object[] { "closure_idiom_fn_params",
            "fn asc(comptime T: type) fn (void, T, T) bool {\n" +
            "    return struct {\n" +
            "        pub fn inner(_: void, a: T, b: T) bool {\n" +
            "            return a < b;\n" +
            "        }\n" +
            "    }.inner;\n" +
            "}\n" +
            "fn desc(_: void, a: u8, b: u8) bool {\n" +
            "    return a > b;\n" +
            "}\n" +
            "fn insertionSort(comptime T: type, items: []T, context: anytype, comptime lessThan: fn (@TypeOf(context), T, T) bool) void {\n" +
            "    var i: usize = 1;\n" +
            "    while (i < items.len) : (i += 1) {\n" +
            "        const x = items[i];\n" +
            "        var j = i;\n" +
            "        while (j > 0 and lessThan(context, x, items[j - 1])) : (j -= 1) {\n" +
            "            items[j] = items[j - 1];\n" +
            "        }\n" +
            "        items[j] = x;\n" +
            "    }\n" +
            "}\n" +
            "fn sortBy(comptime T: type, items: []T, comptime lessThan: fn (void, T, T) bool) void {\n" +
            "    insertionSort(T, items, {}, lessThan);\n" +
            "}\n" +
            "fn swap(noalias a: *u8, noalias b: *u8) void {\n" +
            "    const t = a.*;\n" +
            "    a.* = b.*;\n" +
            "    b.* = t;\n" +
            "}\n" +
            "fn kind(comptime T: type) u8 {\n" +
            "    switch (@typeInfo(T)) {\n" +
            "        .int => return 1,\n" +
            "        .comptime_int => comptime {\n" +
            "            return 2;\n" +
            "        },\n" +
            "        else => return 0,\n" +
            "    }\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const inc = struct {\n" +
            "        fn f(x: u8) u8 {\n" +
            "            return x + 1;\n" +
            "        }\n" +
            "    }.f;\n" +
            "    var xs = [_]u8{ 1, 2 };\n" +
            "    const ys = [_]u8{ 10, 20 };\n" +
            "    for (&xs, ys) |*x, y| x.* += y;\n" +
            "    var p: u8 = 1;\n" +
            "    var q: u8 = 0;\n" +
            "    swap(&p, &q);\n" +
            "    var a = [_]u8{ 3, 40, 1 };\n" +
            "    insertionSort(u8, &a, {}, asc(u8));\n" +
            "    var b = [_]u8{ 2, 9, 5 };\n" +
            "    sortBy(u8, &b, desc);\n" +
            "    return a[2] + b[2] + inc(xs[1]) - 22 + p + kind(u8) - 2;\n" +
            "}\n", 42, "" },
        // A nested `switch` as a STATEMENT prong body (std.math.sqrt's `.int => |I| switch (I.signedness) {
        // .unsigned => return …, … }`): its prongs return, which a switch EXPRESSION's may not; a comptime
        // subject and a runtime one. 10 + 20 + 1 + 2 + 0 + 9 = 42.
        new object[] { "nested_statement_switch",
            "fn kind(comptime T: type) u8 {\n" +
            "    switch (@typeInfo(T)) {\n" +
            "        .int => |info| switch (info.signedness) {\n" +
            "            .signed => return 10,\n" +
            "            .unsigned => return 20,\n" +
            "        },\n" +
            "        else => return 1,\n" +
            "    }\n" +
            "}\n" +
            "fn pick(x: u8) u8 {\n" +
            "    switch (x) {\n" +
            "        0 => switch (x + 1) {\n" +
            "            1 => return 2,\n" +
            "            else => {},\n" +
            "        },\n" +
            "        else => {},\n" +
            "    }\n" +
            "    return 0;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return kind(i8) + kind(u16) + kind(bool) + pick(0) + pick(3) + 9;\n" +
            "}\n", 42, "" },
        // std.Io.Writer.print's comptime format SCAN (road-to-zig-std G3, the format engine's first brick): a
        // bare `inline while (true)` around an `inline while (i < fmt.len) : (i += 1)`, a comptime `switch
        // (fmt[i])` over the comptime string whose `break` ends the unrolling, and `comptime var` updates in
        // the body. "a{}b{}c{" has five braces: 5 * 8 + 2 = 42.
        new object[] { "comptime_format_scan",
            "fn countBraces(comptime fmt: []const u8) u8 {\n" +
            "    comptime var i = 0;\n" +
            "    comptime var n = 0;\n" +
            "    inline while (true) {\n" +
            "        inline while (i < fmt.len) : (i += 1) {\n" +
            "            switch (fmt[i]) {\n" +
            "                '{', '}' => break,\n" +
            "                else => {},\n" +
            "            }\n" +
            "        }\n" +
            "        if (i >= fmt.len) break;\n" +
            "        n += 1;\n" +
            "        i += 1;\n" +
            "    }\n" +
            "    return n;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return countBraces(\"a{}b{}c{\") * 8 + 2;\n" +
            "}\n", 42, "" },
        // std.Io.Writer.print's comptime string var (road-to-zig-std G3): `comptime var literal: []const u8 =
        // "";` grown by `literal = literal ++ fmt[0..i];` (a comptime slice of the comptime format string) and by
        // a literal, read through `.len`. "hello!{}" is 8 bytes: 8 * 5 + 2 = 42.
        new object[] { "comptime_string_var",
            "fn literalPart(comptime fmt: []const u8) usize {\n" +
            "    comptime var literal: []const u8 = \"\";\n" +
            "    comptime var i = 0;\n" +
            "    inline while (i < fmt.len) : (i += 1) {\n" +
            "        switch (fmt[i]) {\n" +
            "            '{' => break,\n" +
            "            else => {},\n" +
            "        }\n" +
            "    }\n" +
            "    literal = literal ++ fmt[0..i];\n" +
            "    literal = literal ++ \"!\";\n" +
            "    if (literal.len != 0) {\n" +
            "        literal = literal ++ fmt[i..];\n" +
            "    }\n" +
            "    return literal.len;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return @intCast(literalPart(\"hello{}\") * 5 + 2);\n" +
            "}\n", 42, "" },
        // Nested containers of a reified struct (road-to-zig-std G3, hash_map's `Custom`): `Entry` (with a
        // method) and `Metadata` flatten to `Map__u16_u8__Entry` / `__Metadata` per instance, and Metadata's own
        // TYPE const `FingerPrint = u7` types its field, whose default names the sibling const `free`.
        // 39 + 2 + 1 + 0 = 42.
        new object[] { "reified_nested_containers",
            "fn Map(comptime K: type, comptime V: type) type {\n" +
            "    return struct {\n" +
            "        head: Entry,\n" +
            "        meta: Metadata = .{},\n" +
            "        const Self = @This();\n" +
            "        pub const Entry = struct {\n" +
            "            key: K,\n" +
            "            value: V,\n" +
            "            pub fn sum(e: Entry) u8 {\n" +
            "                return @as(u8, @intCast(e.key)) + e.value;\n" +
            "            }\n" +
            "        };\n" +
            "        const Metadata = packed struct {\n" +
            "            const FingerPrint = u7;\n" +
            "            const free: FingerPrint = 1;\n" +
            "            fingerprint: FingerPrint = free,\n" +
            "            used: u1 = 0,\n" +
            "        };\n" +
            "        pub fn first(self: Self) Entry {\n" +
            "            return self.head;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const M = Map(u16, u8);\n" +
            "    const m: M = .{ .head = .{ .key = 39, .value = 2 } };\n" +
            "    const e: M.Entry = m.first();\n" +
            "    return e.sum() + m.meta.fingerprint + m.meta.used;\n" +
            "}\n", 42, "" },
        // debug.zig's SafetyLock shape, mode-independent: a module-level bool folded through a `switch` over
        // `builtin.cpu.arch`, a type const that is an `if` over two inline enum types, a field default that
        // chooses with an `if`, and a method whose tail after `if (!checked) return;` zig never analyses (it
        // names `.locked`, which the other arm's enum lacks). 0 + 40 + 2 (locked) + 0 (unknown) = 42.
        new object[] { "comptime_type_arms",
            "const builtin = @import(\"builtin\");\n" +
            "const checked = switch (builtin.cpu.arch) {\n" +
            "    .avr, .msp430 => false,\n" +
            "    else => true,\n" +
            "};\n" +
            "const unchecked = !checked;\n" +
            "const Guard = struct {\n" +
            "    state: State = if (checked) .unlocked else .unknown,\n" +
            "    pub const State = if (checked) enum { unknown, unlocked, locked } else enum { unknown };\n" +
            "    pub fn lock(l: *Guard) void {\n" +
            "        if (!checked) return;\n" +
            "        l.state = .locked;\n" +
            "    }\n" +
            "    pub fn isLocked(l: Guard) bool {\n" +
            "        if (!checked) return false;\n" +
            "        return l.state == .locked;\n" +
            "    }\n" +
            "};\n" +
            "const Off = struct {\n" +
            "    state: State = if (unchecked) .unlocked else .unknown,\n" +
            "    pub const State = if (unchecked) enum { unknown, unlocked, locked } else enum { unknown };\n" +
            "    pub fn lock(o: *Off) void {\n" +
            "        if (!unchecked) return;\n" +
            "        o.state = .locked;\n" +
            "    }\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    var l: Guard = .{};\n" +
            "    const before: u8 = if (l.isLocked()) 1 else 0;\n" +
            "    l.lock();\n" +
            "    const after: u8 = if (l.isLocked()) 40 else 0;\n" +
            "    var o: Off = .{};\n" +
            "    o.lock();\n" +
            "    return before + after + @as(u8, @intFromEnum(l.state)) + @as(u8, @intFromEnum(o.state));\n" +
            "}\n", 42, "" },
        // A TYPE-returning container member (road-to-zig-std G4, hash_map's FieldIterator): reached through
        // type consts (`KeyIterator = FieldIterator(K)`) and through `Self.FieldIterator(K)`, reified per owner
        // instance and argument, evaluated with the owner's seeds live, and the struct it makes walks the
        // owner's nested `Mark` through a many-pointer. Used entries: 10 + 12 + 1 + 3 = 26, + 16 = 42.
        new object[] { "type_returning_methods",
            "fn Table(comptime K: type, comptime V: type) type {\n" +
            "    return struct {\n" +
            "        keys: [3]K,\n" +
            "        values: [3]V,\n" +
            "        marks: [3]Mark,\n" +
            "        const Self = @This();\n" +
            "        const Mark = struct {\n" +
            "            used: bool,\n" +
            "            pub fn isUsed(m: Mark) bool {\n" +
            "                return m.used;\n" +
            "            }\n" +
            "        };\n" +
            "        pub const KeyIterator = FieldIterator(K);\n" +
            "        pub const ValueIterator = FieldIterator(V);\n" +
            "        fn FieldIterator(comptime T: type) type {\n" +
            "            return struct {\n" +
            "                len: usize,\n" +
            "                marks: [*]const Mark,\n" +
            "                items: [*]const T,\n" +
            "                pub fn next(self: *@This()) ?T {\n" +
            "                    while (self.len > 0) {\n" +
            "                        self.len -= 1;\n" +
            "                        const used = self.marks[0].isUsed();\n" +
            "                        const item = self.items[0];\n" +
            "                        self.marks += 1;\n" +
            "                        self.items += 1;\n" +
            "                        if (used) {\n" +
            "                            return item;\n" +
            "                        }\n" +
            "                    }\n" +
            "                    return null;\n" +
            "                }\n" +
            "            };\n" +
            "        }\n" +
            "        pub fn valueIterator(self: *const Self) ValueIterator {\n" +
            "            return .{ .len = 3, .marks = self.marks[0..].ptr, .items = self.values[0..].ptr };\n" +
            "        }\n" +
            "        pub fn keyIterator(self: *const Self) Self.FieldIterator(K) {\n" +
            "            return .{ .len = 3, .marks = self.marks[0..].ptr, .items = self.keys[0..].ptr };\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var t: Table(u16, u8) = undefined;\n" +
            "    const keys = [3]u16{ 1, 2, 3 };\n" +
            "    const values = [3]u8{ 10, 20, 12 };\n" +
            "    const used = [3]bool{ true, false, true };\n" +
            "    for (0..3) |i| {\n" +
            "        t.keys[i] = keys[i];\n" +
            "        t.values[i] = values[i];\n" +
            "        t.marks[i] = .{ .used = used[i] };\n" +
            "    }\n" +
            "    var sum: u16 = 0;\n" +
            "    var vit = t.valueIterator();\n" +
            "    while (vit.next()) |v| sum += v;\n" +
            "    var kit = t.keyIterator();\n" +
            "    while (kit.next()) |k| sum += k;\n" +
            "    return @intCast(sum + 16);\n" +
            "}\n", 42, "" },
        // array_list's forwarding shape (road-to-zig-std G4): a reified struct passes the comptime OPTIONAL it
        // was instantiated with on to another type call (`Aligned(T, alignment)` naming `AlignedManaged(T,
        // alignment)`), null or a payload, and a type const is named through a type call
        // (`Outer(u16, null).Managed`). (10 + 30) + (2 + 0) = 42.
        new object[] { "comptime_optional_forwarding",
            "fn Inner(comptime T: type, comptime bonus: ?u8) type {\n" +
            "    return struct {\n" +
            "        v: T,\n" +
            "        pub fn total(self: @This()) u8 {\n" +
            "            const extra: u8 = if (bonus) |b| b else 0;\n" +
            "            return @as(u8, @intCast(self.v)) + extra;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "fn Outer(comptime T: type, comptime bonus: ?u8) type {\n" +
            "    return struct {\n" +
            "        inner: Inner(T, bonus),\n" +
            "        pub const Managed = Inner(T, bonus);\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const a: Outer(u16, 30) = .{ .inner = .{ .v = 10 } };\n" +
            "    const b: Outer(u16, null).Managed = .{ .v = 2 };\n" +
            "    return a.inner.total() + b.total();\n" +
            "}\n", 42, "" },
        // GENERIC container methods (road-to-zig-std G4, the W3/W5 method cut lifted): an `anytype` context
        // (hash_map's `ctx: anytype`), a `comptime` value typed by the owner's `K` (array_list's `comptime
        // sentinel: T`), and a static generic through the type. 3 matches * 2 + 2 + 30 + 4 = 42.
        new object[] { "generic_methods",
            "fn Store(comptime K: type) type {\n" +
            "    return struct {\n" +
            "        keys: [4]K,\n" +
            "        len: usize,\n" +
            "        const Self = @This();\n" +
            "        pub fn init() Self {\n" +
            "            return .{ .keys = undefined, .len = 0 };\n" +
            "        }\n" +
            "        pub fn add(self: *Self, key: K) void {\n" +
            "            self.keys[self.len] = key;\n" +
            "            self.len += 1;\n" +
            "        }\n" +
            "        // anytype: the context is duck-typed (hash_map's `ctx: anytype`).\n" +
            "        pub fn countAdapted(self: *const Self, key: anytype, ctx: anytype) usize {\n" +
            "            var n: usize = 0;\n" +
            "            for (self.keys[0..self.len]) |k| {\n" +
            "                if (ctx.eql(key, k)) n += 1;\n" +
            "            }\n" +
            "            return n;\n" +
            "        }\n" +
            "        // comptime value parameter typed by the owner's K (array_list's `comptime sentinel: T`).\n" +
            "        pub fn countOf(self: Self, comptime needle: K) usize {\n" +
            "            var n: usize = 0;\n" +
            "            for (self.keys[0..self.len]) |k| {\n" +
            "                if (k == needle) n += 1;\n" +
            "            }\n" +
            "            return n;\n" +
            "        }\n" +
            "        // a static generic called through the type.\n" +
            "        pub fn widen(comptime T: type, k: K) T {\n" +
            "            return @intCast(k);\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "const ModCtx = struct {\n" +
            "    m: u16,\n" +
            "    pub fn eql(self: ModCtx, a: u16, b: u16) bool {\n" +
            "        return a % self.m == b % self.m;\n" +
            "    }\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    var s = Store(u16).init();\n" +
            "    s.add(3);\n" +
            "    s.add(13);\n" +
            "    s.add(7);\n" +
            "    s.add(3);\n" +
            "    const a = s.countAdapted(@as(u16, 23), ModCtx{ .m = 10 });\n" +
            "    const b = s.countOf(3);\n" +
            "    const w = Store(u16).widen(u32, 30);\n" +
            "    return @intCast(a * 2 + b + w + 4);\n" +
            "}\n", 42, "" },
        // zig's `*[N]T` -> `[*]T` coercion (hash_map's FieldIterator `.metadata = &self.metadata`): the address
        // of an array, a field's or a local's, at a many-pointer sink is its first element's. It used to emit
        // `&arr`, a pointer to the element pointer (CS0266). 40 + 2 + 1 - 1 = 42.
        new object[] { "array_address_to_many_pointer",
            "const Holder = struct {\n" +
            "    vals: [3]u8,\n" +
            "    pub fn first(self: *const Holder) u8 {\n" +
            "        const p: [*]const u8 = &self.vals;\n" +
            "        return p[0] + p[2];\n" +
            "    }\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    var h: Holder = undefined;\n" +
            "    h.vals[0] = 40;\n" +
            "    h.vals[1] = 0;\n" +
            "    h.vals[2] = 2;\n" +
            "    const arr = [3]u8{ 1, 2, 3 };\n" +
            "    const q: [*]const u8 = &arr;\n" +
            "    return h.first() + q[0] - 1;\n" +
            "}\n", 42, "" },
        // Forms from the array_list / std.math probes (road-to-zig-std G4): a `catch |e| switch` over a `!void`
        // nobody binds is a statement switch (a void block prong with a `return`); `test` blocks inside enum and
        // struct bodies are dropped; a `comptime_int` local folds. (10 + 1) + 12 + 2 * 9 + 1 = 42.
        new object[] { "void_catch_switch_and_member_tests",
            "const Order = enum {\n" +
            "    lt,\n" +
            "    eq,\n" +
            "    gt,\n" +
            "    pub fn flip(o: Order) Order {\n" +
            "        return switch (o) {\n" +
            "            .lt => .gt,\n" +
            "            .eq => .eq,\n" +
            "            .gt => .lt,\n" +
            "        };\n" +
            "    }\n" +
            "    test flip {\n" +
            "        _ = Order.lt.flip();\n" +
            "    }\n" +
            "};\n" +
            "const Box = struct {\n" +
            "    n: u8,\n" +
            "    test \"a test inside a struct body\" {}\n" +
            "};\n" +
            "fn shrink(ok: bool) error{OutOfMemory}!void {\n" +
            "    if (!ok) return error.OutOfMemory;\n" +
            "}\n" +
            "fn tryShrink(b: *Box, ok: bool) void {\n" +
            "    shrink(ok) catch |e| switch (e) {\n" +
            "        error.OutOfMemory => {\n" +
            "            b.n += 10;\n" +
            "            return;\n" +
            "        },\n" +
            "    };\n" +
            "    b.n += 1;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const step: comptime_int = 3 * 4;\n" +
            "    var b: Box = .{ .n = 0 };\n" +
            "    tryShrink(&b, false);\n" +
            "    tryShrink(&b, true);\n" +
            "    const o = Order.lt.flip();\n" +
            "    return b.n + step + @as(u8, @intFromEnum(o)) * 9 + 1;\n" +
            "}\n", 42, "" },
        // A container TYPE const carries the width it spelled into `@typeInfo(…).int.bits` (hash_map's
        // Metadata.takeFingerprint): `Hash = u64` gives 64 and `FingerPrint = u7` gives 7, bare and qualified,
        // not the 64 / 8 of the C# types they lower to. (64 - 7) - 57 + 127 - 127 + 42 = 42.
        new object[] { "container_type_const_widths",
            "fn Table(comptime K: type) type {\n" +
            "    return struct {\n" +
            "        k: K,\n" +
            "        pub const Hash = u64;\n" +
            "        const Metadata = packed struct {\n" +
            "            const FingerPrint = u7;\n" +
            "            fingerprint: FingerPrint = 0,\n" +
            "            used: u1 = 0,\n" +
            "            pub fn takeFingerprint(hash: Hash) FingerPrint {\n" +
            "                const hash_bits = @typeInfo(Hash).int.bits;\n" +
            "                const fp_bits = @typeInfo(FingerPrint).int.bits;\n" +
            "                return @as(FingerPrint, @truncate(hash >> (hash_bits - fp_bits)));\n" +
            "            }\n" +
            "        };\n" +
            "        pub fn bits() u8 {\n" +
            "            return @typeInfo(Hash).int.bits - @typeInfo(Metadata.FingerPrint).int.bits;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const T = Table(u16);\n" +
            "    const fp = T.Metadata.takeFingerprint(0xFF00_0000_0000_0000);\n" +
            "    return T.bits() - 57 + fp - 127 + 42;\n" +
            "}\n", 42, "" },
        // std.HashMap's auto-context and hashing forms (road-to-zig-std G4): a container const bound to a
        // closure-idiom function value called as a method, on an instance and through the type; a closure body
        // opening with a `comptime { if (…) @compileError(…); }` guard; `and switch (…) {…}`; `@branchHint`;
        // the curated `std.mem.Alignment` (`comptime .fromByteUnits`, `.forward`, `.toByteUnits`); and a
        // `@typeInfo` binding stepped into its `.pointer` payload. 20 + 2 + 16 + 1 + 1 + 8 - 6 = 42.
        new object[] { "container_fn_consts_and_hints",
            "const std = @import(\"std\");\n" +
            "fn getDoubler(comptime K: type, comptime Context: type) (fn (Context, K) u64) {\n" +
            "    comptime {\n" +
            "        if (K == []const u8) @compileError(\"no slices\");\n" +
            "    }\n" +
            "    return struct {\n" +
            "        fn run(ctx: Context, key: K) u64 {\n" +
            "            _ = ctx;\n" +
            "            return @as(u64, key) * 2;\n" +
            "        }\n" +
            "    }.run;\n" +
            "}\n" +
            "fn Ctx(comptime K: type) type {\n" +
            "    return struct {\n" +
            "        pub const hash = getDoubler(K, @This());\n" +
            "    };\n" +
            "}\n" +
            "const Size = enum { one, many, slice };\n" +
            "fn unique(ptr_is_one: bool, size: Size) bool {\n" +
            "    return ptr_is_one and switch (size) {\n" +
            "        .one, .many => true,\n" +
            "        .slice => false,\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const c: Ctx(u32) = .{};\n" +
            "    const h = c.hash(10);\n" +
            "    const via_type = Ctx(u32).hash(c, 1);\n" +
            "    const a: std.mem.Alignment = comptime .fromByteUnits(8);\n" +
            "    const fwd = a.forward(13);\n" +
            "    const info = @typeInfo(*const u16);\n" +
            "    const Child = info.pointer.child;\n" +
            "    const is_const: u64 = @as(Child, 1);\n" +
            "    var hot: u64 = 0;\n" +
            "    if (unique(true, .many)) {\n" +
            "        @branchHint(.likely);\n" +
            "        hot = 1;\n" +
            "    }\n" +
            "    return @intCast(h + via_type + fwd + is_const + hot + a.toByteUnits() - 6);\n" +
            "}\n", 42, "" },
        // Forms std.hash's auto-hash path reaches (road-to-zig-std G4): `if (c) return a else return b;`,
        // `if (v) |x| return x else |_| return 0;`, `.undefined`/`.null` enum literals with an `inline else`, a
        // comptime type question folding so its `@compileError` guard is never analysed, the curated
        // `std.mem.asBytes`, `&arr` as a `*[N]T` sliced open-ended, `@divExact`, `@call`, and a plain value at
        // an `anyerror!u8` argument. 30 + 5 + 3 + 4 + 4 + 2 + 2 + 12 - 20 = 42.
        new object[] { "hash_path_forms",
            "const std = @import(\"std\");\n" +
            "fn isSlice(comptime T: type) bool {\n" +
            "    return switch (@typeInfo(T)) {\n" +
            "        .pointer => |info| info.size == .slice,\n" +
            "        else => false,\n" +
            "    };\n" +
            "}\n" +
            "fn pick(c: bool) u8 {\n" +
            "    if (c) return 30 else return 1;\n" +
            "}\n" +
            "fn unwrap(v: anyerror!u8) u8 {\n" +
            "    if (v) |x| return x else |_| return 0;\n" +
            "}\n" +
            "fn kind(comptime T: type) u8 {\n" +
            "    return switch (@typeInfo(T)) {\n" +
            "        .undefined, .null => 0,\n" +
            "        .int => 3,\n" +
            "        inline else => 7,\n" +
            "    };\n" +
            "}\n" +
            "fn add(a: u8, b: u8) u8 {\n" +
            "    return a + b;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    if (comptime isSlice(u32)) @compileError(\"u32 is no slice\");\n" +
            "    var key: u32 = 0x01020304;\n" +
            "    _ = &key;\n" +
            "    const bytes = std.mem.asBytes(&key);\n" +
            "    const arr = [4]u8{ 1, 2, 3, 4 };\n" +
            "    const p = &arr;\n" +
            "    const tail = p[2..];\n" +
            "    const q: u8 = @divExact(8, 4);\n" +
            "    const total = pick(true) + unwrap(5) + kind(u32) + @as(u8, @intCast(bytes.len)) + bytes[0] +\n" +
            "        @as(u8, @intCast(tail.len)) + q + @call(.auto, add, .{ 10, 2 });\n" +
            "    return total - 20;\n" +
            "}\n", 42, "" },
        // The comptime engine, E1: a pointer to a comptime aggregate is the aggregate itself, so `comptime total()`
        // mutates a struct through `self: *Acc` and an array through `*[3]u32` (`fill(&buf, 4)`); and at runtime
        // `buf[i]` through a `*[N]T` indexes the elements (it lowered to `buf + i * N`). (32 + 4 + 0 + 5) + 1 = 42.
        new object[] { "comptime_pointer_to_aggregate",
            "const Acc = struct {\n" +
            "    n: u32,\n" +
            "    fn add(self: *Acc, v: u32) void {\n" +
            "        self.n += v;\n" +
            "    }\n" +
            "    fn get(self: *const Acc) u32 {\n" +
            "        return self.n;\n" +
            "    }\n" +
            "};\n" +
            "fn fill(buf: *[3]u32, v: u32) void {\n" +
            "    buf[0] = v;\n" +
            "    buf[2] = v + 1;\n" +
            "}\n" +
            "fn total() u32 {\n" +
            "    var a = Acc{ .n = 0 };\n" +
            "    a.add(30);\n" +
            "    a.add(2);\n" +
            "    var buf = [3]u32{ 0, 0, 0 };\n" +
            "    fill(&buf, 4);\n" +
            "    return a.get() + buf[0] + buf[1] + buf[2];\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const t = comptime total();\n" +
            "    return @intCast(t + 1);\n" +
            "}\n", 42, "" },
        // The comptime engine, E2: a comptime value needed WHILE lowering. An array extent calls a function
        // declared later (`[later(3)]u8`, inside a loop and a labeled block whose names the callee reuses) and
        // a generic (`[lenFor(u8)]u8`, a `comptime_int` loop); `if (comptime blockLen(T)) |bl|` folds a
        // `?comptime_int` both ways. Each callee body lowers on demand, once. 7 + 11 + 32 - 8 = 42.
        new object[] { "comptime_values_during_lowering",
            "fn lenFor(comptime T: type) comptime_int {\n" +
            "    var n: comptime_int = 1;\n" +
            "    var i: comptime_int = 0;\n" +
            "    while (i < @sizeOf(T) + 2) : (i += 1) {\n" +
            "        n *= 2;\n" +
            "    }\n" +
            "    return n;\n" +
            "}\n" +
            "fn blockLen(comptime T: type) ?comptime_int {\n" +
            "    if (@sizeOf(T) > 4) return null;\n" +
            "    return lenFor(T) * 2;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var total: u8 = 0;\n" +
            "    var i: u8 = 0;\n" +
            "    while (i < 2) : (i += 1) {\n" +
            "        const n = blk: {\n" +
            "            var buf: [later(3)]u8 = undefined;\n" +
            "            buf[0] = i;\n" +
            "            break :blk buf.len + buf[0];\n" +
            "        };\n" +
            "        total += @intCast(n);\n" +
            "    }\n" +
            "    var arr: [lenFor(u8)]u8 = undefined;\n" +
            "    arr[0] = 3;\n" +
            "    total += arr[0] + @as(u8, arr.len);\n" +
            "    if (comptime blockLen(u16)) |bl| {\n" +
            "        total += bl;\n" +
            "    }\n" +
            "    if (comptime blockLen(u64)) |bl| {\n" +
            "        total += bl;\n" +
            "    } else {\n" +
            "        total -= 8;\n" +
            "    }\n" +
            "    return total;\n" +
            "}\n" +
            "fn later(k: usize) usize {\n" +
            "    var s: usize = 0;\n" +
            "    var i: usize = 0;\n" +
            "    while (i < k) : (i += 1) {\n" +
            "        s += 1;\n" +
            "    }\n" +
            "    return s;\n" +
            "}\n", 42, "" },
        // The comptime engine, E3: a `comptime var st: ArgState = .{…}` (std.Io.Writer.print's format-engine state)
        // lives across statements; each `comptime st.nextArg(…) orelse …` mutates it through `self: *@This()` (a
        // labeled `orelse init: {…}` block inside, `@popCount` at comptime) and a runtime read sees the value it
        // has there. A runtime `x orelse fb: {…}` runs its block only on null. 21 + 21 + 0 + 0 - 1 + 1 = 42.
        new object[] { "comptime_struct_var",
            "const ArgState = struct {\n" +
            "    next_arg: usize = 0,\n" +
            "    used_args: u32 = 0,\n" +
            "    args_len: usize,\n" +
            "\n" +
            "    pub fn hasUnusedArgs(self: *@This()) bool {\n" +
            "        return @popCount(self.used_args) != self.args_len;\n" +
            "    }\n" +
            "\n" +
            "    pub fn nextArg(self: *@This(), arg_index: ?usize) ?usize {\n" +
            "        const next_index = arg_index orelse init: {\n" +
            "            const arg = self.next_arg;\n" +
            "            self.next_arg += 1;\n" +
            "            break :init arg;\n" +
            "        };\n" +
            "        if (next_index >= self.args_len) {\n" +
            "            return null;\n" +
            "        }\n" +
            "        self.used_args |= @as(u32, 1) << @as(u5, @intCast(next_index));\n" +
            "        return next_index;\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    comptime var st: ArgState = .{ .args_len = 4 };\n" +
            "    const a = comptime st.nextArg(null) orelse 99;\n" +
            "    const b = comptime st.nextArg(2) orelse 99;\n" +
            "    const c = comptime st.nextArg(null) orelse 99;\n" +
            "    var total: u8 = a + b * 10 + c;\n" +
            "    if (comptime st.hasUnusedArgs()) {\n" +
            "        total += 21;\n" +
            "    }\n" +
            "    const d = comptime st.nextArg(null) orelse 7;\n" +
            "    const e = comptime st.nextArg(null) orelse 7;\n" +
            "    const f = comptime st.nextArg(null) orelse 7;\n" +
            "    total += @intCast(d + e + f - 12);\n" +
            "    total += @as(u8, st.next_arg) - 5;\n" +
            "    var maybe: ?u8 = null;\n" +
            "    if (total == 42) maybe = 1;\n" +
            "    const g = maybe orelse fb: {\n" +
            "        total += 100;\n" +
            "        break :fb 0;\n" +
            "    };\n" +
            "    return total - g + 1;\n" +
            "}\n", 42, "" },
        // Comptime UNION values (std.Io.Writer.print's `comptime switch (placeholder.arg)`): `const p = comptime
        // parse(...)` lives in the interpreter, a switch over `p.arg` (comptime or not) selects its prong at lowering
        // time with `|n|` bound to the payload, `p.arg != .number` folds, and a comptime byte-slice field
        // (`.spec = s[1..]`) splices back as a string. 3 + 36 + 3 + 0 + 0 = 42.
        new object[] { "comptime_union_values",
            "const Spec = union(enum) { none, number: usize, named: []const u8 };\n" +
            "const Ph = struct { arg: Spec, width: Spec, spec: []const u8 = \"\" };\n" +
            "fn parse(comptime s: []const u8) Ph {\n" +
            "    if (s.len == 0) return .{ .arg = .{ .none = {} }, .width = .none };\n" +
            "    return .{ .arg = .{ .number = s.len }, .width = .{ .number = 7 }, .spec = s[1..] };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const p = comptime parse(\"abc\");\n" +
            "    const pos = comptime switch (p.arg) {\n" +
            "        .none => null,\n" +
            "        .number => |n| n,\n" +
            "        .named => 99,\n" +
            "    };\n" +
            "    const q = comptime parse(\"\");\n" +
            "    const qpos: ?usize = comptime switch (q.arg) {\n" +
            "        .none => null,\n" +
            "        .number => |n| n,\n" +
            "        .named => 99,\n" +
            "    };\n" +
            "    const w: usize = switch (p.width) {\n" +
            "        .number => |x| x,\n" +
            "        else => 0,\n" +
            "    };\n" +
            "    var total: u8 = @intCast(pos + 36);\n" +
            "    if (qpos == null) total += 3;\n" +
            "    total += @intCast(w - 7);\n" +
            "    if (p.arg != .number) {\n" +
            "        total += 100;\n" +
            "    }\n" +
            "    total += @intCast(p.spec.len - 2);\n" +
            "    if (p.spec[1] != 'c') {\n" +
            "        total += 100;\n" +
            "    }\n" +
            "    return total;\n" +
            "}\n", 42, "" },
        // SIMD vectors (target T5): `@Vector(16, u8)` as .NET's Vector128<byte>, `@splat`, an array loaded at a
        // vector sink, element-wise `+`, a comparison mask, `@reduce(.Or)` / `@reduce(.Max)`, a lane read.
        // 20 + 18 + 4 = 42.
        new object[] { "simd_vectors",
            "pub fn main() u8 {\n" +
            "    const V = @Vector(16, u8);\n" +
            "    const a: V = @splat(3);\n" +
            "    var arr: [16]u8 = undefined;\n" +
            "    for (&arr, 0..) |*e, i| e.* = @intCast(i);\n" +
            "    const b: V = arr;\n" +
            "    const c = a + b;\n" +
            "    const m = b == @as(V, @splat(7));\n" +
            "    var total: u8 = 0;\n" +
            "    if (@reduce(.Or, m)) total += 20;\n" +
            "    total += @reduce(.Max, c);\n" +
            "    total += c[1];\n" +
            "    return total;\n" +
            "}\n", 42, "" },
        // Bool vectors (T5): a list literal at a Vector256<uint> sink, `==` / `<` masks, `@select`, `@reduce` over a
        // mask (.Or / .And) and over numbers (.Add / .Min), a mask lane read. 1 + 2 + 4 + 8 + 10 + 5 = 30.
        new object[] { "simd_masks",
            "pub fn main() u8 {\n" +
            "    const V = @Vector(8, u32);\n" +
            "    const a: V = @splat(5);\n" +
            "    const b: V = .{ 1, 5, 9, 5, 0, 0, 0, 0 };\n" +
            "    const m = a == b;\n" +
            "    const picked = @select(u32, m, a, @as(V, @splat(0)));\n" +
            "    var r: u8 = 0;\n" +
            "    if (@reduce(.Or, m)) r += 1;\n" +
            "    if (!@reduce(.And, m)) r += 2;\n" +
            "    if (m[1] and !m[2]) r += 4;\n" +
            "    const lt = b < a;\n" +
            "    if (@reduce(.Or, lt)) r += 8;\n" +
            "    return r + @as(u8, @intCast(@reduce(.Add, picked))) + @as(u8, @intCast(@reduce(.Min, b + a)));\n" +
            "}\n", 30, "" },
        // `break` / `continue` inside an unrolled `inline for` (a jump past the copies / to the end of one), and a
        // `comptime_int` bound to `anytype` (one instance per value, `@typeInfo` says `.comptime_int`, a
        // `@TypeOf(a)` result folds). 1 + 48 - 7 + 1 + 2 - 3 = 42.
        new object[] { "inline_for_break_comptime_int",
            "fn add(a: anytype, b: anytype) @TypeOf(a) {\n" +
            "    return a + b;\n" +
            "}\n" +
            "fn kind(x: anytype) u8 {\n" +
            "    return switch (@typeInfo(@TypeOf(x))) {\n" +
            "        .comptime_int => 1,\n" +
            "        .int => 2,\n" +
            "        else => 3,\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var sum: u8 = 0;\n" +
            "    inline for (0..3) |i| {\n" +
            "        if (i == 0) continue;\n" +
            "        sum += @intCast(i);\n" +
            "        if (sum > 0) break;\n" +
            "    }\n" +
            "    var n: u8 = 0;\n" +
            "    inline for (0..4) |j| {\n" +
            "        const w = 32 / (1 << j);\n" +
            "        if (w < 16) break;\n" +
            "        n += w;\n" +
            "    }\n" +
            "    const k: u8 = add(3, 4);\n" +
            "    return sum + n - k + kind(5) + kind(@as(u8, 5)) - 3;\n" +
            "}\n", 42, "" },
        // Arrays returned by value (std.mem.reverse's reverseVector): a value generic whose `[N]u8` return spells
        // its comptime parameter (lowered per instance), a call's result binding a TYPED `[4]u8` local, and a
        // plain array-returning function. 40 + 10 + 12 - 1 = 61.
        new object[] { "array_return_values",
            "fn rev(comptime N: usize, a: []const u8) [N]u8 {\n" +
            "    var res: [N]u8 = undefined;\n" +
            "    inline for (0..N) |i| {\n" +
            "        res[i] = a[N - i - 1];\n" +
            "    }\n" +
            "    return res;\n" +
            "}\n" +
            "fn three() [3]u8 {\n" +
            "    return .{ 10, 20, 12 };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const src = [_]u8{ 1, 2, 3, 4 };\n" +
            "    const r: [4]u8 = rev(4, &src);\n" +
            "    const t = three();\n" +
            "    var copy: [4]u8 = undefined;\n" +
            "    @memcpy(&copy, &r);\n" +
            "    return copy[0] * 10 + t[0] + t[2] - r[3];\n" +
            "}\n", 61, "" },
        // std.Io.Writer.printValue / printInt's statement shapes (road-to-zig-std G3): `=> if (c) switch …`,
        // `=> if (x) |v| return v`, `=> for (…) |_| {…}` prongs, and a switch over a TYPE (`switch (@TypeOf(v))`,
        // a comptime_int argument included). 5 + 3 + 4 + 1 + 2 + 3 + 20 = 38.
        new object[] { "prong_forms_type_switch",
            "fn f(x: u8, fmtlen: u8) u8 {\n" +
            "    var r: u8 = 0;\n" +
            "    switch (fmtlen) {\n" +
            "        3 => if (x > 1) switch (x) {\n" +
            "            2 => r = 5,\n" +
            "            else => r = 6,\n" +
            "        },\n" +
            "        4 => if (maybe(x)) |v| return v,\n" +
            "        else => for (0..x) |_| {\n" +
            "            r += 1;\n" +
            "        },\n" +
            "    }\n" +
            "    return r;\n" +
            "}\n" +
            "fn maybe(x: u8) ?u8 {\n" +
            "    return if (x > 2) x else null;\n" +
            "}\n" +
            "fn kind(v: anytype) u8 {\n" +
            "    switch (@TypeOf(v)) {\n" +
            "        u8, u16 => return 1,\n" +
            "        comptime_int => return 2,\n" +
            "        else => return 3,\n" +
            "    }\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return f(2, 3) + f(3, 4) + f(4, 9) + kind(@as(u8, 1)) + kind(7) + kind(@as(i64, 1)) + 20;\n" +
            "}\n", 38, "" },
        // std.Io.Writer.print's argument bookkeeping (G3): a comptime union selects `.none => null`, bound as a
        // comptime OPTIONAL, handed to a comptime method on a comptime struct var, whose `orelse @compileError(…)`
        // fallback is never analysed. 0 + 42.
        new object[] { "comptime_optional_switch",
            "const ArgState = struct {\n" +
            "    next_arg: usize = 0,\n" +
            "    used_args: u32 = 0,\n" +
            "    args_len: usize,\n" +
            "    pub fn nextArg(self: *@This(), arg_index: ?usize) ?usize {\n" +
            "        const next_index = arg_index orelse init: {\n" +
            "            const arg = self.next_arg;\n" +
            "            self.next_arg += 1;\n" +
            "            break :init arg;\n" +
            "        };\n" +
            "        if (next_index >= self.args_len) {\n" +
            "            return null;\n" +
            "        }\n" +
            "        self.used_args |= @as(u32, 1) << @as(u5, @intCast(next_index));\n" +
            "        return next_index;\n" +
            "    }\n" +
            "};\n" +
            "const Spec = union(enum) { none, number: usize };\n" +
            "fn parse(comptime s: []const u8) Spec {\n" +
            "    if (s.len == 0) return .{ .none = {} };\n" +
            "    return .{ .number = s.len };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    comptime var st: ArgState = .{ .args_len = 1 };\n" +
            "    const p = comptime parse(\"\");\n" +
            "    const arg_pos = comptime switch (p) {\n" +
            "        .none => null,\n" +
            "        .number => |pos| pos,\n" +
            "    };\n" +
            "    const a = comptime st.nextArg(arg_pos) orelse @compileError(\"too few arguments\");\n" +
            "    return @intCast(a + 42);\n" +
            "}\n", 42, "" },
        // `@field(args, "1")` over a tuple (std.Io.Writer.print names a field by position), and std.math.cast's
        // comptime-settled `is_comptime or …` whose right side is never analysed. 41 + 1, then 42. The name is a
        // literal, not `field_names[i]`: CI's zig 0.16.0 spells the member list `fields`, 0.17-dev `field_names`
        // (the list-index form is unit-pinned in ZigFormatEngineTests).
        new object[] { "tuple_field_by_name",
            "fn pick(args: anytype) u8 {\n" +
            "    return @field(args, \"1\");\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return pick(.{ @as(u8, 1), @as(u8, 41) }) + 1;\n" +
            "}\n", 42, "" },
        new object[] { "comptime_or_short_circuit",
            "fn wide(x: anytype) bool {\n" +
            "    const is_comptime = @TypeOf(x) == comptime_int;\n" +
            "    return is_comptime or @typeInfo(@TypeOf(x)).int.bits > 8;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return if (wide(5)) 42 else 0;\n" +
            "}\n", 42, "" },
        // std.fmt's integer printing, reduced (road-to-zig-std G3, task #50): a compound assignment through `.?`,
        // an array stored through a slice deref, an array returned from one (digits2), and an `unreachable` switch
        // arm. 34 + 4 + 2 + 1 = 41.
        new object[] { "fmt_stores_and_returns",
            "fn digits2(value: u8) [2]u8 {\n" +
            "    return \"00010203040506070809101112131415161718192021222324252627282930313233343536373839404142\"[value * 2 ..][0..2].*;\n" +
            "}\n" +
            "fn toChar(d: u8) u8 {\n" +
            "    return switch (d) {\n" +
            "        0...9 => d + '0',\n" +
            "        else => unreachable,\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var r: ?u32 = 3;\n" +
            "    r.? *= 10;\n" +
            "    r.? += 4;\n" +
            "    var buf: [4]u8 = .{ 0, 0, 0, 0 };\n" +
            "    buf[1..][0..2].* = digits2(42);\n" +
            "    return @intCast(r.? + buf[1] - '0' + buf[2] - '0' + toChar(1) - '0');\n" +
            "}\n", 41, "" },
        // A struct declared inside a generic with METHODS (std.sort's local `Context`, task #48): its methods read the
        // instance's comptime comparator, a `void` context field has no storage, and a comparator is named through its
        // container (`Rev.gt`). 1*10 + 9 + 9*2 + 1 = 38.
        new object[] { "local_struct_methods",
            "fn sortWith(comptime T: type, items: []T, context: anytype, comptime lessThanFn: fn (@TypeOf(context), T, T) bool) void {\n" +
            "    const Context = struct {\n" +
            "        items: []T,\n" +
            "        sub_ctx: @TypeOf(context),\n" +
            "        pub fn lessThan(ctx: @This(), a: usize, b: usize) bool {\n" +
            "            return lessThanFn(ctx.sub_ctx, ctx.items[a], ctx.items[b]);\n" +
            "        }\n" +
            "        pub fn swap(ctx: @This(), a: usize, b: usize) void {\n" +
            "            const t = ctx.items[a];\n" +
            "            ctx.items[a] = ctx.items[b];\n" +
            "            ctx.items[b] = t;\n" +
            "        }\n" +
            "    };\n" +
            "    const ctx = Context{ .items = items, .sub_ctx = context };\n" +
            "    var i: usize = 1;\n" +
            "    while (i < items.len) : (i += 1) {\n" +
            "        var j = i;\n" +
            "        while (j > 0 and ctx.lessThan(j, j - 1)) : (j -= 1) ctx.swap(j, j - 1);\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "fn lt(_: void, a: u8, b: u8) bool {\n" +
            "    return a < b;\n" +
            "}\n" +
            "\n" +
            "const Rev = struct {\n" +
            "    fn gt(_: Rev, a: u8, b: u8) bool {\n" +
            "        return a > b;\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var xs = [_]u8{ 5, 3, 9, 1 };\n" +
            "    sortWith(u8, &xs, {}, lt);\n" +
            "    var ys = xs;\n" +
            "    sortWith(u8, &ys, Rev{}, Rev.gt);\n" +
            "    return xs[0] * 10 + xs[3] + ys[0] * 2 + ys[3];\n" +
            "}\n", 38, "" },
        // Arrays are VALUES: `var b = a;`, `const c: [3]u8 = a;` and `d = a;` copy, so a later write to one is not
        // seen through another (they used to alias one buffer, a silent miscompile). 9 + 1 + 8 + 1 + 1 + 2 = 22.
        new object[] { "array_value_copies",
            "pub fn main() u8 {\n" +
            "    var a = [_]u8{ 1, 2, 3 };\n" +
            "    var b = a;\n" +
            "    const c: [3]u8 = a;\n" +
            "    var d: [3]u8 = undefined;\n" +
            "    d = a;\n" +
            "    a[0] = 9;\n" +
            "    b[1] = 8;\n" +
            "    return a[0] + b[0] + b[1] + c[0] + d[0] + d[1];\n" +
            "}\n", 22, "" },
        // `@ptrCast` of a single-item pointer to a byte slice (std.mem.swap) and a folded comptime_int past `long`
        // (std.math.sqrt_int's `maxInt(T)`). 1 + 4 + 4 = 9.
        new object[] { "ptrcast_slice_wide_comptime_int",
            "fn big(comptime T: type) comptime_int {\n" +
            "    return (1 << @bitSizeOf(T)) - 1;\n" +
            "}\n" +
            "fn bytes(comptime T: type, p: *T) []u8 {\n" +
            "    return @ptrCast(p);\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const m = big(u64);\n" +
            "    var x: u32 = 0x01020304;\n" +
            "    const b = bytes(u32, &x);\n" +
            "    return @intCast((m % 7) + b.len + b[0]);\n" +
            "}\n", 9, "" },
        // std.hash_map's shapes (task #51): a packed struct of u7 + u1 bit-fields `@bitCast` to a byte, a struct swapped
        // through byte views, a slice at a `*const [4]u8` parameter and `@bitCast` of the array, a u128 product of two
        // derefs, an error union of an optional, `errdefer comptime unreachable`, `return @intCast(…)` in a `!u8`.
        // 0 + 10 + 2 + 1 + 12 + 4 + 3 + 7 = 39.
        new object[] { "hash_map_shapes",
            "const Meta = packed struct {\n" +
            "    fingerprint: u7 = 0,\n" +
            "    used: u1 = 0,\n" +
            "};\n" +
            "\n" +
            "const Pair = struct { a: u64, b: u32 };\n" +
            "\n" +
            "fn swapBytes(comptime T: type, a: *T, b: *T) void {\n" +
            "    const a_bytes: []u8 = @ptrCast(a);\n" +
            "    const b_bytes: []u8 = @ptrCast(b);\n" +
            "    for (a_bytes, b_bytes) |*x, *y| {\n" +
            "        const t = x.*;\n" +
            "        x.* = y.*;\n" +
            "        y.* = t;\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "fn readU32(bytes: *const [4]u8) u32 {\n" +
            "    return @bitCast(bytes.*);\n" +
            "}\n" +
            "\n" +
            "fn wide(a: *const u64, b: *const u64) u128 {\n" +
            "    return @as(u128, a.*) * b.*;\n" +
            "}\n" +
            "\n" +
            "fn find(xs: []const u8, x: u8) !?usize {\n" +
            "    for (xs, 0..) |v, i| {\n" +
            "        if (v == x) return i;\n" +
            "    }\n" +
            "    return null;\n" +
            "}\n" +
            "\n" +
            "fn fill(out: []u8) !u8 {\n" +
            "    errdefer comptime unreachable;\n" +
            "    out[0] = 7;\n" +
            "    return @intCast(out.len);\n" +
            "}\n" +
            "\n" +
            "pub fn main() !u8 {\n" +
            "    const m = Meta{ .fingerprint = 5, .used = 1 };\n" +
            "    const as_byte: u8 = @bitCast(m);\n" +
            "    var p = Pair{ .a = 1, .b = 2 };\n" +
            "    var q = Pair{ .a = 10, .b = 20 };\n" +
            "    swapBytes(Pair, &p, &q);\n" +
            "    const data = [_]u8{ 1, 0, 0, 0, 9, 9 };\n" +
            "    const r = readU32(data[0..4]);\n" +
            "    const x: u64 = 3;\n" +
            "    const y: u64 = 4;\n" +
            "    const w = wide(&x, &y);\n" +
            "    const idx = (try find(&data, 9)) orelse 99;\n" +
            "    var buf: [3]u8 = undefined;\n" +
            "    const n = try fill(&buf);\n" +
            "    return @intCast(as_byte - 133 + p.a + q.b + r + @as(u64, @intCast(w)) + idx + n + buf[0]);\n" +
            "}\n", 39, "" },
        // std's string-helper shapes (tasks #52 to #55): a struct field typed by a comptime switch over an enum-literal
        // type argument, a local struct chosen by an if-capture over a comptime optional, an `and` settling an unrolled
        // `if`, a string literal tuple element sliced without its NUL, a value `if (switch …) |i|`, `@bitCast` of a
        // slice's `[0..4].*`. 3 + 2 + 16 + 1 + 32 + 3 + 5 + 2 + 99 + 3 = 166.
        new object[] { "string_shapes",
            "fn contains(s: []const u8, c: u8) bool {\n" +
            "    for (s) |d| {\n" +
            "        if (d == c) return true;\n" +
            "    }\n" +
            "    return false;\n" +
            "}\n" +
            "\n" +
            "const Kind = enum { any, scalar };\n" +
            "\n" +
            "fn Split(comptime k: Kind) type {\n" +
            "    return struct {\n" +
            "        buffer: []const u8,\n" +
            "        index: usize,\n" +
            "        delimiter: switch (k) {\n" +
            "            .any => []const u8,\n" +
            "            .scalar => u8,\n" +
            "        },\n" +
            "\n" +
            "        pub fn count(self: *@This()) usize {\n" +
            "            var n: usize = 0;\n" +
            "            while (self.index < self.buffer.len) : (self.index += 1) {\n" +
            "                const c = self.buffer[self.index];\n" +
            "                const hit = switch (k) {\n" +
            "                    .any => contains(self.delimiter, c),\n" +
            "                    .scalar => c == self.delimiter,\n" +
            "                };\n" +
            "                if (hit) n += 1;\n" +
            "            }\n" +
            "            return n;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "fn chunkSize(comptime vec: ?usize) usize {\n" +
            "    const Scan = if (vec) |v|\n" +
            "        struct {\n" +
            "            pub const size = v;\n" +
            "        }\n" +
            "    else\n" +
            "        struct {\n" +
            "            pub const size = 1;\n" +
            "        };\n" +
            "    return Scan.size;\n" +
            "}\n" +
            "\n" +
            "fn widest(a: []const u8) usize {\n" +
            "    inline for (1..6) |s| {\n" +
            "        const n = 4 << s;\n" +
            "        if (n <= 32 and a.len <= n) {\n" +
            "            return n;\n" +
            "        }\n" +
            "    }\n" +
            "    return 7;\n" +
            "}\n" +
            "\n" +
            "fn firstLen(args: anytype) usize {\n" +
            "    const s: []const u8 = @field(args, \"0\");\n" +
            "    return s.len;\n" +
            "}\n" +
            "\n" +
            "fn find(s: []const u8, c: u8) ?usize {\n" +
            "    for (s, 0..) |d, i| {\n" +
            "        if (d == c) return i;\n" +
            "    }\n" +
            "    return null;\n" +
            "}\n" +
            "\n" +
            "fn firstHit(comptime k: Kind, s: []const u8) usize {\n" +
            "    return if (switch (k) {\n" +
            "        .any => find(s, ';'),\n" +
            "        .scalar => find(s, ','),\n" +
            "    }) |i| i else 99;\n" +
            "}\n" +
            "\n" +
            "fn word(s: []const u8) u32 {\n" +
            "    const w: u32 = @bitCast(s[0..4].*);\n" +
            "    return w % 7;\n" +
            "}\n" +
            "\n" +
            "fn helper() usize {\n" +
            "    return 5;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var sp = Split(.scalar){ .buffer = \"a,b,,c\", .index = 0, .delimiter = ',' };\n" +
            "    var sa = Split(.any){ .buffer = \"a;b,c\", .index = 0, .delimiter = \",;\" };\n" +
            "    const buf = \"abcdefghijklmnopqrst\";\n" +
            "    const total = sp.count() + sa.count() + chunkSize(16) + chunkSize(null) + widest(buf) + firstLen(.{ \"abc\", 1 }) + helper() + firstHit(.any, \"ab;c\") + firstHit(.scalar, \"abc\") + word(\"abcd\");\n" +
            "    return @intCast(total);\n" +
            "}\n", 166, "" },
        // ArrayList.appendSlice of `&.{ 3, 4, 5 }` (the list literal result-located at u16) viewed through
        // std.mem.sliceAsBytes: 6 bytes summing to 12, so 18.
        new object[] { "list_slice_as_bytes",
            "const std = @import(\"std\");\n" +
            "\n" +
            "pub fn main() !u8 {\n" +
            "    const alloc = std.heap.page_allocator;\n" +
            "    var list: std.ArrayList(u16) = .empty;\n" +
            "    defer list.deinit(alloc);\n" +
            "    try list.appendSlice(alloc, &.{ 3, 4, 5 });\n" +
            "    const bytes = std.mem.sliceAsBytes(list.items);\n" +
            "    var sum: usize = 0;\n" +
            "    for (bytes) |b| sum += b;\n" +
            "    return @intCast(bytes.len + sum);\n" +
            "}\n", 18, "" },
        // A root file naming itself (road-to-zig-std, task #56): `const root = @This();`, then root.helper(),
        // root.Point (as a type and for a static call), root.limit, and root.helper as a function value, bare
        // and by address. It prints (stderr is compared only on exit 0) helper(7) + 3 + twice(&helper, 7) + 40 = 60.
        new object[] { "root_self_alias",
            "const std = @import(\"std\");\n" +
            "const root = @This();\n" +
            "\n" +
            "const limit: u8 = 3;\n" +
            "\n" +
            "fn helper(x: u8) u8 {\n" +
            "    return x + 1;\n" +
            "}\n" +
            "\n" +
            "const Point = struct {\n" +
            "    x: u8,\n" +
            "    pub fn sum(self: Point) u8 {\n" +
            "        return self.x + root.limit;\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "fn twice(f: *const fn (u8) u8, x: u8) u8 {\n" +
            "    return f(f(x));\n" +
            "}\n" +
            "\n" +
            "fn shadow() u8 {\n" +
            "    const r = struct {\n" +
            "        const limit: u8 = 40;\n" +
            "    };\n" +
            "    return r.limit;\n" +
            "}\n" +
            "\n" +
            "pub fn main() void {\n" +
            "    const p: root.Point = .{ .x = 4 };\n" +
            "    const f = root.helper;\n" +
            "    const via_type = root.Point.sum(p);\n" +
            "    const total = root.helper(p.sum()) + root.limit + twice(&root.helper, via_type) + shadow();\n" +
            "    std.debug.print(\"{d} {d} {d}\\n\", .{ via_type, twice(f, 1), total });\n" +
            "}\n", 0,
            "7 3 60" },
        // std's small helpers' shapes (road-to-zig-std, tasks #57 to #59): @TypeOf over several operands, a local
        // `switch (T)` over floats dotcc does not lower, a value switch / if as a field value, `&.{ i.base + 'a' }` as a
        // comptime string unrolled by `inline for`, `if (c) return x else y`, dupe and `&[0]u8{}` at a `![]u8` return.
        new object[] { "std_helper_shapes",
            "const std = @import(\"std\");\n" +
            "\n" +
            "fn clampLike(val: anytype, lower: anytype, upper: anytype) @TypeOf(val, lower, upper) {\n" +
            "    return @max(lower, @min(val, upper));\n" +
            "}\n" +
            "\n" +
            "fn mantissaSize(comptime T: type) usize {\n" +
            "    const M = switch (T) {\n" +
            "        f16, f32, f64 => u64,\n" +
            "        f80, f128 => u128,\n" +
            "        else => unreachable,\n" +
            "    };\n" +
            "    if (T == f16 or T == f32 or T == f64) {\n" +
            "        return @sizeOf(M);\n" +
            "    }\n" +
            "    return 1;\n" +
            "}\n" +
            "\n" +
            "const Info = struct { base: u8, max: u8 };\n" +
            "\n" +
            "fn info(comptime T: type) Info {\n" +
            "    return .{ .base = switch (T) {\n" +
            "        u64 => 10,\n" +
            "        else => 16,\n" +
            "    }, .max = if (T == u64) 19 else 38 };\n" +
            "}\n" +
            "\n" +
            "fn hasAny(s: []const u8, comptime cs: []const u8) bool {\n" +
            "    for (s) |c| {\n" +
            "        inline for (cs) |d| if (c == d) return true;\n" +
            "    }\n" +
            "    return false;\n" +
            "}\n" +
            "\n" +
            "fn hasSep(s: []const u8, comptime i: Info) bool {\n" +
            "    return hasAny(s, &.{i.base + 'a'});\n" +
            "}\n" +
            "\n" +
            "fn firstOrNull(s: []const u8) ?u8 {\n" +
            "    return if (s.len > 0) return s[0] else null;\n" +
            "}\n" +
            "\n" +
            "fn dupeOrEmpty(a: std.mem.Allocator, z: bool) ![]u8 {\n" +
            "    return if (z) try a.dupe(u8, &[1]u8{7}) else &[0]u8{};\n" +
            "}\n" +
            "\n" +
            "pub fn main() !u8 {\n" +
            "    const a = std.heap.page_allocator;\n" +
            "    const d = try dupeOrEmpty(a, true);\n" +
            "    defer a.free(d);\n" +
            "    const e = try dupeOrEmpty(a, false);\n" +
            "    const none = [_]u8{};\n" +
            "    const i = comptime info(u64);\n" +
            "    const c: u16 = clampLike(@as(u16, 300), 1, 250);\n" +
            "    var total: usize = c - 200;\n" +
            "    total += mantissaSize(f64) + i.base + i.max + d[0] + e.len + none.len;\n" +
            "    total += @intFromBool(hasSep(\"xk\", i)) + @intFromBool(hasSep(\"xy\", i));\n" +
            "    total += (firstOrNull(\"A\") orelse 0) - 'A' + 1;\n" +
            "    const missing: usize = if (firstOrNull(\"\")) |_| 100 else 2;\n" +
            "    total += missing;\n" +
            "    return @intCast(total);\n" +
            "}\n", 98, "" },
        // A captured value `if` on the right of a compound assignment (task #62): `+=` / `*=` are hoist points like `=`.
        new object[] { "compound_assign_capture_if",
            "fn firstOrNull(s: []const u8) ?u8 {\n" +
            "    if (s.len == 0) return null;\n" +
            "    return s[0];\n" +
            "}\n" +
            "\n" +
            "var calls: u8 = 0;\n" +
            "fn bump() u8 {\n" +
            "    calls += 1;\n" +
            "    return calls;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var total: u8 = 1;\n" +
            "    total += if (firstOrNull(\"\")) |_| 100 else 2;\n" +
            "    total += if (firstOrNull(\"A\")) |c| c - 60 else 50;\n" +
            "    total *= if (firstOrNull(\"B\")) |_| 2 else 3;\n" +
            "    var buf = [_]u8{ 0, 0 };\n" +
            "    buf[1] += if (firstOrNull(\"x\")) |c| c else 0;\n" +
            "    buf[bump() - 1] += 3;\n" +
            "    return total + buf[0] + calls;\n" +
            "}\n", 20, "" },
        // Backed packed structs (road-to-zig-std, task #61): `packed struct(u8)` bit-fields `@bitCast` to their byte, a
        // returned `packed struct(u16)`, `MaskInt == u0` folding away, `-%x`, `@ctz` into a `?usize`, `~@as(u8, 0)`.
        new object[] { "packed_struct_backed",
            "const Flags = packed struct(u8) {\n" +
            "    read: u1,\n" +
            "    write: u1,\n" +
            "    mode: u6,\n" +
            "};\n" +
            "\n" +
            "fn Mask(comptime n: u16) type {\n" +
            "    return packed struct(u16) {\n" +
            "        const Self = @This();\n" +
            "        pub const MaskInt = u16;\n" +
            "        bits: MaskInt,\n" +
            "\n" +
            "        pub fn count(self: Self) usize {\n" +
            "            if (MaskInt == u0) return 0;\n" +
            "            _ = n;\n" +
            "            return @popCount(self.bits);\n" +
            "        }\n" +
            "\n" +
            "        pub fn first(self: Self) ?usize {\n" +
            "            if (self.bits == 0) return null;\n" +
            "            return @ctz(self.bits);\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "fn fill(comptime T: type, on: bool) T {\n" +
            "    return -%@as(T, @intFromBool(on));\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const f = Flags{ .read = 1, .write = 0, .mode = 5 };\n" +
            "    const raw: u8 = @bitCast(f);\n" +
            "    const m = Mask(16){ .bits = 0b1011000 };\n" +
            "    const full = fill(u8, true);\n" +
            "    const none = fill(u8, false);\n" +
            "    const all: u8 = ~@as(u8, 0);\n" +
            "    return raw + @as(u8, @intCast(m.count())) + @as(u8, @intCast(m.first().?)) + (full - 250) + none + (all - 255);\n" +
            "}\n", 32, "" },
        // std.Io.Writer.Allocating's shapes (road-to-zig-std, task #60): a vtable const of the container's own functions,
        // `&vtable` in static storage (not a copy on a returning frame), @fieldParentPtr, rawAlloc / rawFree with
        // @returnAddress() and Alignment.of, and a value `if (r) |n| … else |_| …` over an error union.
        new object[] { "writer_shapes",
            "const std = @import(\"std\");\n" +
            "\n" +
            "const Sink = struct {\n" +
            "    const VTable = struct {\n" +
            "        put: *const fn (s: *Inner, v: u8) void,\n" +
            "        reset: *const fn (s: *Inner) void,\n" +
            "    };\n" +
            "    const Inner = struct {\n" +
            "        vtable: *const VTable,\n" +
            "        last: u8,\n" +
            "    };\n" +
            "\n" +
            "    total: u32,\n" +
            "    inner: Inner,\n" +
            "\n" +
            "    const vtable: VTable = .{\n" +
            "        .put = Sink.put,\n" +
            "        .reset = resetInner,\n" +
            "    };\n" +
            "\n" +
            "    fn put(s: *Inner, v: u8) void {\n" +
            "        const self: *Sink = @fieldParentPtr(\"inner\", s);\n" +
            "        self.total += v;\n" +
            "        s.last = v;\n" +
            "    }\n" +
            "\n" +
            "    fn resetInner(s: *Inner) void {\n" +
            "        const self: *Sink = @fieldParentPtr(\"inner\", s);\n" +
            "        self.total = 0;\n" +
            "    }\n" +
            "\n" +
            "    fn init() Sink {\n" +
            "        return .{ .total = 0, .inner = .{ .vtable = &vtable, .last = 0 } };\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "fn sizeOr(r: anyerror!usize) usize {\n" +
            "    return if (r) |n| n * 2 else |_| 7;\n" +
            "}\n" +
            "\n" +
            "fn failing() anyerror!usize {\n" +
            "    return error.Nope;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var s = Sink.init();\n" +
            "    s.inner.vtable.put(&s.inner, 5);\n" +
            "    s.inner.vtable.put(&s.inner, 9);\n" +
            "    const after_puts = s.total + s.inner.last;\n" +
            "    s.inner.vtable.reset(&s.inner);\n" +
            "\n" +
            "    const a = std.heap.page_allocator;\n" +
            "    const alignment: std.mem.Alignment = .of(u64);\n" +
            "    const raw = a.rawAlloc(16, alignment, @returnAddress()) orelse return 1;\n" +
            "    raw[0] = 3;\n" +
            "    const first = raw[0];\n" +
            "    a.rawFree(raw[0..16], alignment, @returnAddress());\n" +
            "\n" +
            "    const ok: anyerror!usize = 4;\n" +
            "    return @intCast(after_puts + s.total + first + alignment.toByteUnits() + sizeOr(ok) + sizeOr(failing()));\n" +
            "}\n", 49, "" },
        // A type-returning function's own `const MantissaT = mantissaType(T);` used by its methods, a generic one too
        // (std.fmt.parse_float's BiasedFp.toFloat), and a type comparison folded as a call argument (task #59).
        new object[] { "type_body_alias_methods",
            "fn assert(ok: bool) void {\n" +
            "    if (!ok) unreachable;\n" +
            "}\n" +
            "\n" +
            "fn mantissaType(comptime T: type) type {\n" +
            "    return switch (T) {\n" +
            "        f16, f32, f64 => u64,\n" +
            "        f80, f128 => u128,\n" +
            "        else => unreachable,\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "fn Biased(comptime T: type) type {\n" +
            "    const MantissaT = mantissaType(T);\n" +
            "    return struct {\n" +
            "        const Self = @This();\n" +
            "        f: MantissaT,\n" +
            "        e: i32,\n" +
            "\n" +
            "        pub fn word(self: Self) MantissaT {\n" +
            "            var w: MantissaT = self.f;\n" +
            "            w |= @as(MantissaT, @intCast(self.e)) << 8;\n" +
            "            return w;\n" +
            "        }\n" +
            "\n" +
            "        pub fn scaled(self: Self, comptime k: u8) MantissaT {\n" +
            "            return self.f * @as(MantissaT, k);\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "fn check(comptime T: type) bool {\n" +
            "    assert(T == f16 or T == f32 or T == f64);\n" +
            "    return @sizeOf(T) == 8;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const b = Biased(f64){ .f = 3, .e = 1 };\n" +
            "    const w = b.word();\n" +
            "    return @intCast(w % 256 + (w >> 8) + b.scaled(4) + @intFromBool(check(f64)));\n" +
            "}\n", 17, "" },
        // A labeled switch expression (road-to-zig-std, task #59; std.math.shl): value prongs, `break :r v` in block
        // prongs, a nested switch keeping its own prongs, an `unreachable` prong.
        new object[] { "labeled_switch",
            "fn shiftCap(comptime T: type, amt: u32) u32 {\n" +
            "    const capped = capped: switch (@typeInfo(T)) {\n" +
            "        .int => |info| {\n" +
            "            if (amt < info.bits) break :capped amt;\n" +
            "            break :capped info.bits - 1;\n" +
            "        },\n" +
            "        else => 0,\n" +
            "    };\n" +
            "    return capped;\n" +
            "}\n" +
            "\n" +
            "fn classify(x: u8) u8 {\n" +
            "    const r = r: switch (x) {\n" +
            "        0 => 10,\n" +
            "        1...9 => {\n" +
            "            if (x == 5) break :r 55;\n" +
            "            break :r x * 2;\n" +
            "        },\n" +
            "        else => 99,\n" +
            "    };\n" +
            "    return r;\n" +
            "}\n" +
            "\n" +
            "var hits: u8 = 0;\n" +
            "\n" +
            "fn nested(x: u8) u8 {\n" +
            "    const r: u8 = r: switch (x) {\n" +
            "        0 => {\n" +
            "            switch (x) {\n" +
            "                0 => {\n" +
            "                    hits += 1;\n" +
            "                },\n" +
            "                else => {\n" +
            "                    hits += 2;\n" +
            "                },\n" +
            "            }\n" +
            "            break :r 4;\n" +
            "        },\n" +
            "        1 => unreachable,\n" +
            "        else => 6,\n" +
            "    };\n" +
            "    return r;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    return @intCast(shiftCap(u8, 3) + shiftCap(u8, 40) + classify(0) + classify(5) + classify(7) + classify(200) % 100 + nested(0) + nested(9) + hits);\n" +
            "}\n", 199, "" },
        // std.fmt.parse_float's shapes (task #59): a comptime-only struct from a type-argument call feeding a comptime
        // argument, `switch (@typeInfo(T).float.bits)` with a `@compileError` prong, a reified struct's const sizing
        // its array field, a labeled switch over a comptime subject.
        new object[] { "parse_float_shapes",
            "const Info = struct { explicit_bits: comptime_int, bias: comptime_int };\n" +
            "\n" +
            "fn mantissaBits(comptime T: type) comptime_int {\n" +
            "    return switch (@typeInfo(T).float.bits) {\n" +
            "        32 => 23,\n" +
            "        64 => 52,\n" +
            "        else => @compileError(\"unknown floating point type \" ++ @typeName(T)),\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "fn infoOf(comptime T: type) Info {\n" +
            "    return switch (T) {\n" +
            "        f32 => .{ .explicit_bits = mantissaBits(T), .bias = 127 },\n" +
            "        f64 => .{ .explicit_bits = mantissaBits(T), .bias = 1023 },\n" +
            "        else => @compileError(\"no info\"),\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "fn product(comptime precision: u8, w: u64) u64 {\n" +
            "    return w * precision;\n" +
            "}\n" +
            "\n" +
            "fn Decimal(comptime T: type) type {\n" +
            "    const Wide = if (T == f64) u64 else u32;\n" +
            "    return struct {\n" +
            "        const Self = @This();\n" +
            "        pub const max_digits = if (Wide == u64) 12 else 6;\n" +
            "        digits: [max_digits]u8,\n" +
            "        count: usize,\n" +
            "\n" +
            "        pub fn init() Self {\n" +
            "            var v: Self = undefined;\n" +
            "            v.count = 0;\n" +
            "            return v;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "fn capped(comptime T: type, amt: u32) u32 {\n" +
            "    return capped: switch (@typeInfo(T)) {\n" +
            "        .int => |info| {\n" +
            "            if (amt < info.bits) break :capped amt;\n" +
            "            break :capped info.bits - 1;\n" +
            "        },\n" +
            "        else => 0,\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const info = infoOf(f64);\n" +
            "    const p = product(info.explicit_bits + 3, 2);\n" +
            "    var d = Decimal(f64).init();\n" +
            "    d.digits[11] = 4;\n" +
            "    d.count = d.digits.len;\n" +
            "    return @intCast(p + d.count + d.digits[11] + capped(u8, 3) + capped(u8, 40) + capped(bool, 1));\n" +
            "}\n", 136, "" },
        // std.fmt.parseFloat's last walls (task #59), three of them silent wrong answers: `1 << 52` of an untyped literal,
        // `opt orelse error.E` as an error union, `buf.* = @bitCast(v)` through a `*[8]u8`; plus `while (true)`, an f32
        // table, a switch-chosen type alias's width, `Gen(T).CONST`, and `break :blk switch (x) { 0 => break, … }`.
        new object[] { "parse_float_core",
            "const E = error{Bad};\n" +
            "\n" +
            "fn bits(comptime T: type) comptime_int {\n" +
            "    return switch (@typeInfo(T).float.bits) {\n" +
            "        32 => 32,\n" +
            "        64 => 64,\n" +
            "        else => @compileError(\"no\"),\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "fn widthOf(comptime Type: type) u32 {\n" +
            "    const R = switch (Type) {\n" +
            "        else => Type,\n" +
            "        comptime_float => f64,\n" +
            "    };\n" +
            "    return bits(R);\n" +
            "}\n" +
            "\n" +
            "fn shifted(n: u6) u64 {\n" +
            "    return (1 << 52) | (@as(u64, 1) << n);\n" +
            "}\n" +
            "\n" +
            "fn half(x: ?u8) E!u8 {\n" +
            "    return x orelse error.Bad;\n" +
            "}\n" +
            "\n" +
            "fn firstBig(xs: []const u8) u8 {\n" +
            "    var i: usize = 0;\n" +
            "    while (true) {\n" +
            "        if (xs[i] > 10) return xs[i];\n" +
            "        i += 1;\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "fn store(buf: *[8]u8, v: u64) void {\n" +
            "    buf.* = @bitCast(v);\n" +
            "}\n" +
            "\n" +
            "fn Table(comptime T: type) type {\n" +
            "    return struct {\n" +
            "        pub const len = if (T == f32) 3 else 5;\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "fn pick(xs: []const u8) u8 {\n" +
            "    var i: usize = 0;\n" +
            "    var total: u8 = 0;\n" +
            "    while (i < xs.len) : (i += 1) {\n" +
            "        const add: u8 = blk: {\n" +
            "            break :blk switch (xs[i]) {\n" +
            "                0 => break,\n" +
            "                1, 2 => 10,\n" +
            "                else => 1,\n" +
            "            };\n" +
            "        };\n" +
            "        total += add;\n" +
            "    }\n" +
            "    return total;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const pows = [_]f32{ 1e0, 1e1, 1e2 };\n" +
            "    var buf: [8]u8 = undefined;\n" +
            "    store(&buf, 0x0102030405060708);\n" +
            "    var total: u64 = 0;\n" +
            "    total += widthOf(f32) + widthOf(f64);\n" +
            "    total += (shifted(3) >> 52) + (shifted(3) & 0xff);\n" +
            "    total += half(7) catch 0;\n" +
            "    total += half(null) catch 100;\n" +
            "    total += firstBig(&.{ 1, 2, 30, 4 });\n" +
            "    total += buf[0] + buf[7];\n" +
            "    total += Table(f32).len + Table(f64).len;\n" +
            "    total += @intFromFloat(pows[2]);\n" +
            "    total += pick(&.{ 1, 2, 3, 0, 1 });\n" +
            "    return @intCast(total % 256);\n" +
            "}\n", 124, "" },
        // A compound assignment as a switch prong body (task #64): `0 => hits += 1`, `*=`, `|=`, `-%=`, `<<=`.
        new object[] { "compound_assign_prong",
            "var hits: u32 = 0;\n" +
            "pub fn main() u8 {\n" +
            "    var bits: u8 = 0b0001;\n" +
            "    for ([_]u8{ 0, 1, 2, 3, 1, 0 }) |x| {\n" +
            "        switch (x) {\n" +
            "            0 => hits += 1,\n" +
            "            1 => hits *= 3,\n" +
            "            2 => bits |= 0b1000,\n" +
            "            else => hits -%= 1,\n" +
            "        }\n" +
            "    }\n" +
            "    var v: u8 = 7;\n" +
            "    switch (bits) {\n" +
            "        9 => v <<= 2,\n" +
            "        else => v = 0,\n" +
            "    }\n" +
            "    return @intCast(hits + bits + v);\n" +
            "}\n", 44, "" },
        // std.debug.print `{s}` of a byte slice (task #65): exactly `.len` bytes, a const, a mutable and a sub-slice
        // whose bytes are not NUL-terminated.
        new object[] { "debug_print_slice",
            "const std = @import(\"std\");\n" +
            "pub fn main() void {\n" +
            "    const word: []const u8 = \"hello world\";\n" +
            "    var buf = [_]u8{ 'a', 'b', 'c', 'd' };\n" +
            "    const mut: []u8 = buf[1..3];\n" +
            "    std.debug.print(\"[{s}] [{s}] [{s}]\\n\", .{ word[0..5], mut, word[6..] });\n" +
            "}\n", 0,
            "[hello] [bc] [world]" },
        // Float builtins (tasks #67/#68/#69): @round rounds half away from zero (C#'s default is to even), an f32 stays
        // on MathF, @setRuntimeSafety is a no-op, and `while (true) : (i -= 1)` must read as a loop that never falls out.
        new object[] { "float_builtins",
            "fn check(s: *u32, ok: bool, bit: u5) void {\n" +
            "    if (ok) s.* |= @as(u32, 1) << bit;\n" +
            "}\n" +
            "\n" +
            "fn lastZero(s: []const u8) ?usize {\n" +
            "    var i: usize = s.len;\n" +
            "    while (true) : (i -= 1) {\n" +
            "        if (i == 0) return null;\n" +
            "        if (s[i - 1] == 0) return i - 1;\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    @setRuntimeSafety(false);\n" +
            "    var s: u32 = 0;\n" +
            "    var half: f64 = 2.5;\n" +
            "    var neg_half: f64 = -2.5;\n" +
            "    var neg: f64 = -2.7;\n" +
            "    var x32: f32 = 6.25;\n" +
            "    _ = .{ &half, &neg_half, &neg, &x32 };\n" +
            "    check(&s, @round(half) == 3.0, 0);\n" +
            "    check(&s, @round(neg_half) == -3.0, 1);\n" +
            "    check(&s, @trunc(neg) == -2.0, 2);\n" +
            "    check(&s, @floor(neg) == -3.0, 3);\n" +
            "    check(&s, @ceil(neg) == -2.0, 4);\n" +
            "    check(&s, @sqrt(half * 10.0) == 5.0, 5);\n" +
            "    check(&s, @exp2(@as(f64, 3.0)) == 8.0, 6);\n" +
            "    check(&s, @log2(@as(f64, 8.0)) == 3.0, 7);\n" +
            "    check(&s, @abs(neg) == 2.7, 8);\n" +
            "    check(&s, @sin(@as(f64, 0.0)) == 0.0, 9);\n" +
            "    check(&s, @sqrt(x32) == 2.5, 10);\n" +
            "    check(&s, @round(x32) == 6.0, 11);\n" +
            "    check(&s, @log10(@as(f64, 1000.0)) == 3.0, 12);\n" +
            "    check(&s, @exp(@as(f64, 0.0)) == 1.0, 13);\n" +
            "    check(&s, @abs(@as(f32, -0.5)) == 0.5, 14);\n" +
            "    const z: u32 = @intCast(lastZero(&[_]u8{ 1, 0, 2, 0, 5 }).?);\n" +
            "    return @intCast((s + z) % 251);\n" +
            "}\n", 140, "" },
        // A struct's layout as a comptime tag, and packed-struct `==` over the backing storage (task #70).
        new object[] { "struct_layout_packed_eq",
            "const Flags = packed struct { lo: u4, hi: u4 };\n" +
            "const Ext = extern struct { p: u32 };\n" +
            "const Plain = struct { p: u32 };\n" +
            "fn kind(comptime T: type) u8 {\n" +
            "    return switch (@typeInfo(T).@\"struct\".layout) {\n" +
            "        .auto => 1,\n" +
            "        .@\"extern\" => 2,\n" +
            "        .@\"packed\" => 3,\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const a = Flags{ .lo = 1, .hi = 2 };\n" +
            "    const b = Flags{ .lo = 1, .hi = 2 };\n" +
            "    return kind(Plain) * 100 + kind(Ext) * 10 + kind(Flags) + @intFromBool(a == b);\n" +
            "}\n", 124, "" },
        // A global and a container const computed by a labeled block run at compile time (task #79): the static holds
        // the values, filled through `for (&t, 0..) |*e, i|` element pointers in the comptime interpreter.
        new object[] { "labeled_block_consts",
            "const table = blk: {\n" +
            "    var t: [4]u32 = undefined;\n" +
            "    for (&t, 0..) |*e, i| {\n" +
            "        e.* = @as(u32, @intCast(i)) * 5;\n" +
            "    }\n" +
            "    break :blk t;\n" +
            "};\n" +
            "const S = struct {\n" +
            "    const inner = blk: {\n" +
            "        var t: [4]u32 = undefined;\n" +
            "        for (&t, 0..) |*e, i| {\n" +
            "            e.* = @as(u32, @intCast(i)) + 1;\n" +
            "        }\n" +
            "        break :blk t;\n" +
            "    };\n" +
            "    fn get(i: usize) u32 {\n" +
            "        return inner[i];\n" +
            "    }\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    var i: usize = 3;\n" +
            "    _ = &i;\n" +
            "    return @truncate(table[i] + S.get(i));\n" +
            "}\n", 19, "" },
        // A type-returning generic with a comptime STRUCT value parameter (task #71, std.hash.crc's Crc(W, algorithm)):
        // the struct keys the instance, and its block-built table const and methods read the struct's fields.
        new object[] { "comptime_struct_type_param",
            "fn Algo(comptime W: type) type {\n" +
            "    return struct { poly: W, initial: W, refl: bool };\n" +
            "}\n" +
            "fn Crc(comptime W: type, comptime a: Algo(W)) type {\n" +
            "    return struct {\n" +
            "        const Self = @This();\n" +
            "        const table = blk: {\n" +
            "            var t: [4]W = undefined;\n" +
            "            for (&t, 0..) |*e, i| {\n" +
            "                e.* = @as(W, @intCast(i)) * a.poly;\n" +
            "            }\n" +
            "            break :blk t;\n" +
            "        };\n" +
            "        crc: W,\n" +
            "        pub fn init() Self {\n" +
            "            const v = if (a.refl) a.initial + 1 else a.initial;\n" +
            "            return Self{ .crc = v };\n" +
            "        }\n" +
            "        pub fn hash(b: []const u8) W {\n" +
            "            var c = init();\n" +
            "            for (b) |x| c.crc = table[x & 3] ^ (c.crc >> 1);\n" +
            "            return c.crc;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "const C = Crc(u32, .{ .poly = 5, .initial = 7, .refl = true });\n" +
            "pub fn main() u8 {\n" +
            "    return @truncate(C.hash(\"hello\"));\n" +
            "}\n", 15, "" },
        // @bitReverse at the operand's declared width (task #71): a u3 in its byte reverses three bits.
        new object[] { "bit_reverse",
            "pub fn main() u8 {\n" +
            "    var x: u8 = 0b0000_0110;\n" +
            "    var y: u3 = 0b011;\n" +
            "    _ = .{ &x, &y };\n" +
            "    return @bitReverse(x) + @as(u8, @bitReverse(y));\n" +
            "}\n", 102, "" },
        // A returned `if` / switch with an error arm is the error union itself (task #72; the whole of it had been
        // wrapped as a success, returning the error's code as the payload).
        new object[] { "error_arm_returns",
            "fn pick(x: u8) !u8 {\n" +
            "    return if (x < 10) x * 2 else error.TooBig;\n" +
            "}\n" +
            "fn kind(x: u8) !u8 {\n" +
            "    return switch (x) {\n" +
            "        0 => 7,\n" +
            "        1...5 => x + 1,\n" +
            "        else => error.Bad,\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const a = pick(3) catch 100;\n" +
            "    const b = pick(30) catch 100;\n" +
            "    const c = kind(0) catch 50;\n" +
            "    const d = kind(4) catch 50;\n" +
            "    const e = kind(9) catch 50;\n" +
            "    return a + b + c + d + e;\n" +
            "}\n", 168, "" },
        // `two(s[0..2].*)`: a comptime-length slice dereference passes to a `[2]u8` parameter (task #72, the shape of
        // std.unicode.utf8Decode's `utf8Decode2(bytes[0..2].*)`).
        new object[] { "slice_deref_array_arg",
            "fn two(b: [2]u8) u16 {\n" +
            "    return @as(u16, b[0]) * 256 + b[1];\n" +
            "}\n" +
            "fn pair(s: []const u8) u16 {\n" +
            "    return two(s[0..2].*) + two(s[1..3].*);\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return @truncate(pair(\"\\x01\\x02\\x03\"));\n" +
            "}\n", 5, "" },
        // A switch choosing a type argument and a @sizeOf operand (task #73).
        new object[] { "switch_type_argument",
            "fn id(comptime T: type, x: T) T {\n" +
            "    return x;\n" +
            "}\n" +
            "fn widen(comptime T: type, v: u8) u32 {\n" +
            "    return @as(u32, @intCast(id(switch (@typeInfo(T).int.signedness) {\n" +
            "        .signed => i32,\n" +
            "        .unsigned => u32,\n" +
            "    }, v))) + @as(u32, @sizeOf(switch (@typeInfo(T).int.signedness) {\n" +
            "        .signed => i64,\n" +
            "        .unsigned => u16,\n" +
            "    }));\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return @truncate(widen(u8, 7) + widen(i8, 9));\n" +
            "}\n", 26, "" },
        // std.debug.print `{}` / `{any}` of a bool prints `true` / `false` (task #81), and a local bound to a comparison is a
        // zig `bool`.
        new object[] { "debug_print_bool",
            "const std = @import(\"std\");\n" +
            "pub fn main() void {\n" +
            "    var x: u8 = 3;\n" +
            "    _ = &x;\n" +
            "    const t = x > 2;\n" +
            "    const f = x == 9;\n" +
            "    std.debug.print(\"{} {} {any} [{}]\\n\", .{ t, f, x < 1, !f });\n" +
            "}\n", 0,
            "true false false [true]" },
        // A comptime signedness tag bound to a const drives @Int (task #76, std.mem.readVarInt's shape).
        new object[] { "signedness_tag_binding",
            "fn widen(comptime T: type, v: T) u16 {\n" +
            "    const signedness = @typeInfo(T).int.signedness;\n" +
            "    const W = @Int(signedness, 16);\n" +
            "    const w: W = v;\n" +
            "    return @bitCast(w);\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return @truncate(widen(i8, -3) +% widen(u8, 200));\n" +
            "}\n", 197, "" },
        // A type chosen by a comptime `if` equals its arm in a type switch (task #77; it had silently taken `else`),
        // `&array_global` is its storage pointer, and `.len` reads through a pointer to an array.
        new object[] { "type_if_alias",
            "const small = [_]u8{ 1, 2, 3 };\n" +
            "const big = [_]u8{ 7, 8, 9, 10 };\n" +
            "fn pick(comptime T: type) u8 {\n" +
            "    const DT = if (@bitSizeOf(T) <= 64) u64 else u128;\n" +
            "    const k: u8 = switch (DT) {\n" +
            "        u64 => 1,\n" +
            "        u128 => 2,\n" +
            "        else => 3,\n" +
            "    };\n" +
            "    const tables = switch (DT) {\n" +
            "        u64 => &small,\n" +
            "        u128 => &big,\n" +
            "        else => unreachable,\n" +
            "    };\n" +
            "    return @sizeOf(DT) + k + tables[1] + @as(u8, @intCast(tables.len));\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return pick(f64) + pick(u128);\n" +
            "}\n", 44, "" },
        // The curated ArrayList's index and capacity members (task #74): insert, orderedRemove, swapRemove,
        // ensureUnusedCapacity, appendAssumeCapacity, shrinkRetainingCapacity.
        new object[] { "array_list_index_members",
            "const std = @import(\"std\");\n" +
            "\n" +
            "pub fn main() !u8 {\n" +
            "    const a = std.heap.page_allocator;\n" +
            "    var l: std.ArrayList(u16) = .empty;\n" +
            "    defer l.deinit(a);\n" +
            "    try l.appendSlice(a, &.{ 10, 20, 30, 40, 50 });\n" +
            "    try l.insert(a, 0, 5);\n" +
            "    try l.insert(a, 3, 25);\n" +
            "    const o = l.orderedRemove(1);\n" +
            "    const s = l.swapRemove(0);\n" +
            "    try l.ensureUnusedCapacity(a, 2);\n" +
            "    l.appendAssumeCapacity(60);\n" +
            "    l.appendAssumeCapacity(70);\n" +
            "    const last = l.items[l.items.len - 1];\n" +
            "    l.shrinkRetainingCapacity(4);\n" +
            "    const lastOr = l.items[l.items.len - 1];\n" +
            "    var sum: u32 = 0;\n" +
            "    for (l.items, 0..) |x, i| sum += x * @as(u32, @intCast(i + 1));\n" +
            "    std.debug.print(\"{d} {d} {d} {d} {d} {d}\\n\", .{ o, s, last, lastOr, l.items.len, sum });\n" +
            "    return @intCast((sum + o + s + last + lastOr) % 251);\n" +
            "}\n", 149, "" },
        // A struct literal setting array fields, in a body: the literal is a hoisted temp and each array field is copied
        // in after it (task #78; it had been a loud cut, and a C# object initializer cannot set a fixed buffer).
        new object[] { "array_field_literal",
            "const Inner = struct { x: u16, y: i8 };\n" +
            "const Outer = struct { a: u32, in: Inner, arr: [3]u8, words: [2]u32 };\n" +
            "fn make(k: u8) Outer {\n" +
            "    return Outer{ .a = 1, .in = .{ .x = 2, .y = -3 }, .arr = .{ k, k + 1, k + 2 }, .words = .{ 100, 200 } };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const o = make(4);\n" +
            "    var p = Outer{ .a = 9, .in = .{ .x = 1, .y = 1 }, .arr = o.arr, .words = undefined };\n" +
            "    p.words[1] = 7;\n" +
            "    return @truncate(o.a + o.arr[0] + o.arr[2] + o.words[1] + p.arr[1] + p.words[1]);\n" +
            "}\n", 223, "" },
        // A global struct with array fields (task #78) is built by a synthesized initializer; `"ABCD".*` is the array
        // value, @splat fills a `[N]T`, and an index takes a cast builtin's type (task #75, std.base64's shapes).
        new object[] { "global_array_field_init",
            "const alphabet = \"ABCD\".*;\n" +
            "const Codec = struct { chars: [4]u8, table: [8]u8, pad: u8 };\n" +
            "const codec = Codec{ .chars = alphabet, .table = @splat(0xff), .pad = '=' };\n" +
            "pub fn main() u8 {\n" +
            "    return codec.chars[@truncate(codec.pad & 3)] +% codec.table[7];\n" +
            "}\n", 65, "" },
        // `catch |e| return switch (e) { … }` (task #80, grammar): the returned value is a switch over the error.
        new object[] { "catch_return_switch",
            "const E = error{ Truncated, BadStart, Other };\n" +
            "fn step(x: u8) E!u8 {\n" +
            "    return switch (x) {\n" +
            "        0 => error.Truncated,\n" +
            "        1 => error.BadStart,\n" +
            "        2 => error.Other,\n" +
            "        else => x * 2,\n" +
            "    };\n" +
            "}\n" +
            "fn code(x: u8) u32 {\n" +
            "    const v = step(x) catch |e| return switch (e) {\n" +
            "        error.Truncated => 900,\n" +
            "        error.BadStart => 901,\n" +
            "        else => 902,\n" +
            "    };\n" +
            "    return v;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return @truncate(code(0) + code(1) + code(2) + code(7));\n" +
            "}\n", 157, "" },
        // `comptime first: { … break :first a ++ b; }` (task #82): a comptime labeled block builds a static table (array
        // values joined by `++`), and a shift amount takes a cast builtin's type.
        new object[] { "comptime_labeled_block",
            "fn classify(c: u8) u8 {\n" +
            "    const xx = 0xF1;\n" +
            "    const as = 0xF0;\n" +
            "    const first = comptime first: {\n" +
            "        const a: [4]u8 = @splat(as);\n" +
            "        const b: [4]u8 = @splat(xx);\n" +
            "        break :first a ++ b;\n" +
            "    };\n" +
            "    return first[c & 7] +% (@as(u8, 1) << @intCast(c & 3));\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return classify(1) +% classify(6);\n" +
            "}\n", 231, "" },
        // A switch over a comptime bool (task #84): `const W = switch (wide) { true => u32, false => u8 };` and a value switch,
        // folded per instance.
        new object[] { "comptime_bool_switch",
            "fn id(comptime T: type, x: T) T {\n" +
            "    return x;\n" +
            "}\n" +
            "fn widen(comptime wide: bool, v: u8) u32 {\n" +
            "    const W = switch (wide) {\n" +
            "        true => u32,\n" +
            "        false => u8,\n" +
            "    };\n" +
            "    const k: u32 = switch (wide) {\n" +
            "        true => 1000,\n" +
            "        false => 1,\n" +
            "    };\n" +
            "    return @as(u32, id(W, v)) + @as(u32, @sizeOf(W)) * k;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return @truncate(widen(true, 7) + widen(false, 9));\n" +
            "}\n", 177, "" },
        // `inline a, b => |n|` prongs of a runtime switch (task #87), `else => |U| U` over a type (task #86), and a string
        // literal passed as an `anytype` reading its logical length (it had read one more, the NUL).
        new object[] { "inline_prongs_type_capture",
            "fn tail(n: usize, bytes: []const u8) u32 {\n" +
            "    var acc: u32 = 7;\n" +
            "    switch (n) {\n" +
            "        inline 0, 1, 2 => |count| {\n" +
            "            inline for (0..count) |i| acc = acc *% 31 +% bytes[i];\n" +
            "            return acc;\n" +
            "        },\n" +
            "        inline 3...5 => |count| {\n" +
            "            acc +%= @as(u32, count) * 1000;\n" +
            "            inline for (0..count) |i| acc +%= bytes[i];\n" +
            "            return acc;\n" +
            "        },\n" +
            "        else => return 0,\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "fn length(x: anytype) usize {\n" +
            "    return x.len;\n" +
            "}\n" +
            "\n" +
            "fn Widened(comptime T: type) type {\n" +
            "    return switch (T) {\n" +
            "        comptime_int => u64,\n" +
            "        else => |U| U,\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const s = \"abcdef\";\n" +
            "    var total: u32 = 0;\n" +
            "    var n: usize = 0;\n" +
            "    while (n <= 6) : (n += 1) total +%= tail(n, s);\n" +
            "    const w: Widened(u16) = 40000;\n" +
            "    return @truncate(total +% @as(u32, @intCast(length(\"hello\") + length(s))) +% w);\n" +
            "}\n", 136, "" },
        // Task #88 (std.bit_set's ArrayBitSet shapes): `return extern struct`, value loops whose `else` returns (by value and
        // by `|*x|` capture), a value `while` with a continue-expression, and a const labeled block building a struct whose
        // array field is non-zero. Also pins two silent miscompiles: `~@as(u64, 0) >> pad` sign-extended to all ones, and the
        // struct's array field left zeroed.
        new object[] { "extern_struct_value_loops",
            "const std = @import(\"std\");\n" +
            "\n" +
            "fn Masks(comptime n: usize) type {\n" +
            "    return extern struct {\n" +
            "        const Self = @This();\n" +
            "        const pad = 64 * n - 100;\n" +
            "        const last = ~@as(u64, 0) >> pad;\n" +
            "        words: [n]u64,\n" +
            "        const full: Self = full: {\n" +
            "            var w: [n]u64 = @splat(~@as(u64, 0));\n" +
            "            w[n - 1] = last;\n" +
            "            break :full .{ .words = w };\n" +
            "        };\n" +
            "        fn firstSet(self: Self) ?usize {\n" +
            "            var offset: usize = 0;\n" +
            "            const word = for (self.words) |word| {\n" +
            "                if (word != 0) break word;\n" +
            "                offset += 64;\n" +
            "            } else return null;\n" +
            "            return offset + @ctz(word);\n" +
            "        }\n" +
            "        fn clearFirst(self: *Self) ?usize {\n" +
            "            var offset: usize = 0;\n" +
            "            const word = for (&self.words) |*word| {\n" +
            "                if (word.* != 0) break word;\n" +
            "                offset += 64;\n" +
            "            } else return null;\n" +
            "            const index = @ctz(word.*);\n" +
            "            word.* &= word.* - 1;\n" +
            "            return offset + index;\n" +
            "        }\n" +
            "        fn same(self: Self, other: Self) bool {\n" +
            "            var i: usize = 0;\n" +
            "            return while (i < n) : (i += 1) {\n" +
            "                if (self.words[i] != other.words[i]) break false;\n" +
            "            } else true;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const M = Masks(2);\n" +
            "    var m = M.full;\n" +
            "    var total: usize = @popCount(m.words[0]) + @popCount(m.words[1]);\n" +
            "    total += m.firstSet() orelse 99;\n" +
            "    total += m.clearFirst() orelse 99;\n" +
            "    total += m.firstSet() orelse 99;\n" +
            "    total += @as(usize, @intFromBool(m.same(m))) * 10 + @as(usize, @intFromBool(m.same(M.full))) * 20;\n" +
            "    const empty = M{ .words = @splat(0) };\n" +
            "    total += empty.firstSet() orelse 7;\n" +
            "    return @truncate(total);\n" +
            "}\n", 118, "" },
        // Task #90 (std.crypto.sha2 shapes): inline `asm` in a prong whose comptime condition folds false (parsed, never lowered),
        // a comptime ARRAY argument to a type-returning generic read from a const global, an implicitly comptime
        // `comptime_int` parameter, `align(N)` fields, and `inline for` over a comptime array value.
        new object[] { "asm_dead_prong_comptime_array_param",
            "const builtin = @import(\"builtin\");\n" +
            "\n" +
            "const Seed = [4]u32;\n" +
            "const seed_a = Seed{ 3, 5, 7, 11 };\n" +
            "\n" +
            "const Param = struct { a: usize, k: u32 };\n" +
            "\n" +
            "fn Mixer(comptime seed: Seed, rounds: comptime_int) type {\n" +
            "    return struct {\n" +
            "        const Self = @This();\n" +
            "        s: [4]u32 align(16),\n" +
            "        count: u32 align(8) = 0,\n" +
            "\n" +
            "        fn init() Self {\n" +
            "            return .{ .s = seed };\n" +
            "        }\n" +
            "\n" +
            "        fn step(self: *Self) void {\n" +
            "            const params = comptime [_]Param{ .{ .a = 0, .k = 1 }, .{ .a = 2, .k = 3 } };\n" +
            "            inline for (params) |p| {\n" +
            "                self.s[p.a] = self.s[p.a] *% 31 +% p.k;\n" +
            "            }\n" +
            "            switch (builtin.cpu.arch) {\n" +
            "                .x86_64, .aarch64 => if (builtin.zig_backend == .stage2_c) {\n" +
            "                    asm volatile (\"nop\");\n" +
            "                    return;\n" +
            "                },\n" +
            "                else => {},\n" +
            "            }\n" +
            "            self.count += rounds;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var m = Mixer(seed_a, 5).init();\n" +
            "    m.step();\n" +
            "    m.step();\n" +
            "    return @truncate(m.s[0] +% m.s[2] +% m.count);\n" +
            "}\n", 20, "" },
        // Task #89 (std.enums.valuesFromFields shape): an inline function whose `comptime { …; return &final; }` block builds a
        // slice from a `comptime []const comptime_int` argument, as u8 and as an enum. The slice is a pinned static of the evaluated
        // elements (it had dangled into the frame, and the array copy had been skipped to zeros).
        new object[] { "comptime_block_static_slice",
            "const Color = enum(u8) { red = 4, green = 9, blue = 2 };\n" +
            "\n" +
            "inline fn table(comptime fv: []const comptime_int) []const u8 {\n" +
            "    comptime {\n" +
            "        var result: [fv.len]u8 = undefined;\n" +
            "        for (&result, fv) |*r, f| {\n" +
            "            r.* = @intCast(f * 2);\n" +
            "        }\n" +
            "        const final = result;\n" +
            "        return &final;\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "inline fn colors(comptime fv: []const comptime_int) []const Color {\n" +
            "    comptime {\n" +
            "        var result: [fv.len]Color = undefined;\n" +
            "        for (&result, fv) |*r, f| {\n" +
            "            r.* = @enumFromInt(f);\n" +
            "        }\n" +
            "        const final = result;\n" +
            "        return &final;\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const t = table(&.{ 5, 6, 7 });\n" +
            "    const c = colors(&.{ 2, 9 });\n" +
            "    return t[0] + t[1] + t[2] + @intFromEnum(c[0]) * 10 + @intFromEnum(c[1]);\n" +
            "}\n", 65, "" },
        // Task #83: comptime_int arithmetic past 64 bits (`0xFFFF_FFFF_FFFF_FFFF + 1`) folds to a comptime_int; a type-returning
        // generic runs `@inComptime()` code and `@subWithOverflow` in its body; a `break` of a void call leaves a plain loop;
        // a comptime-const switch case label folds to the subject's type.
        new object[] { "comptime_int_wide_and_comptime_generic_body",
            "fn swapInts(a: *u32, b: *u32) void {\n" +
            "    if (@inComptime()) {\n" +
            "        const tmp = a.*;\n" +
            "        a.* = b.*;\n" +
            "        b.* = tmp;\n" +
            "    } else {\n" +
            "        const tmp = a.*;\n" +
            "        a.* = b.*;\n" +
            "        b.* = tmp;\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "fn bump(counter: *u32) void {\n" +
            "    counter.* += 3;\n" +
            "}\n" +
            "\n" +
            "fn Table(comptime n: comptime_int) type {\n" +
            "    var vals = [_]u32{ 30, n, 20 };\n" +
            "    swapInts(&vals[0], &vals[2]);\n" +
            "    const diff = @subWithOverflow(vals[1], vals[0]);\n" +
            "    const lo = vals[0];\n" +
            "    const wrapped = diff[1];\n" +
            "    return struct {\n" +
            "        const first = lo;\n" +
            "        const flag = wrapped;\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "fn classify(x: usize) u8 {\n" +
            "    const limit = 4 * 3;\n" +
            "    return switch (x) {\n" +
            "        0 => 1,\n" +
            "        limit => 2,\n" +
            "        else => 3,\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const big = 0xFFFF_FFFF_FFFF_FFFF + 1;\n" +
            "    var counter: u32 = 0;\n" +
            "    while (true) {\n" +
            "        if (counter > 5) break bump(&counter);\n" +
            "        counter += 1;\n" +
            "    }\n" +
            "    const T = Table(10);\n" +
            "    const top: u64 = @intCast(big >> 60);\n" +
            "    return @intCast(top + counter + T.first + @as(u64, T.flag) * 100 + classify(12) + classify(0));\n" +
            "}\n", 148, "" },
        // Task #93: `@Struct` reified as a type-returning function's result (a spelled name list, `&@splat(T)`,
        // a per-field type list, default values through `.default_value_ptr`); `@tagName` of a comptime enum value,
        // then `@field` by that name; a comptime struct parameter spelled in a generic method's return type.
        new object[] { "reify_struct_and_comptime_tag_name",
            "const Color = enum { red, green, blue };\n" +
            "\n" +
            "fn FieldStruct(comptime Data: type, comptime def: ?Data) type {\n" +
            "    const default_ptr: ?*const anyopaque = if (def) |d| @ptrCast(&d) else null;\n" +
            "    return @Struct(.auto, null, &.{ \"red\", \"green\", \"blue\" }, &@splat(Data), &@splat(.{ .default_value_ptr = default_ptr }));\n" +
            "}\n" +
            "\n" +
            "fn Pair(comptime A: type, comptime B: type) type {\n" +
            "    return @Struct(.auto, null, &.{ \"x\", \"y\" }, &.{ A, B }, &.{ .{}, .{} });\n" +
            "}\n" +
            "\n" +
            "fn sum(s: FieldStruct(u8, 7)) u8 {\n" +
            "    return s.red + s.green * 10 + s.blue;\n" +
            "}\n" +
            "\n" +
            "fn keyFor(i: usize) Color {\n" +
            "    return @enumFromInt(i);\n" +
            "}\n" +
            "\n" +
            "const Opts = struct {\n" +
            "    step: u8 = 1,\n" +
            "};\n" +
            "\n" +
            "fn Bits(comptime n: u8) type {\n" +
            "    return struct {\n" +
            "        mask: u8 = n,\n" +
            "\n" +
            "        pub fn iter(self: *const @This(), comptime options: Opts) Iter(options) {\n" +
            "            _ = self;\n" +
            "            return .{};\n" +
            "        }\n" +
            "\n" +
            "        pub fn Iter(comptime options: Opts) type {\n" +
            "            return struct {\n" +
            "                at: u8 = options.step,\n" +
            "                pub fn next(self: *@This()) u8 {\n" +
            "                    self.at += options.step;\n" +
            "                    return self.at;\n" +
            "                }\n" +
            "            };\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    // @Struct: a spelled name list, `&@splat` types and default pointers, a per-field list.\n" +
            "    const flags: FieldStruct(bool, false) = .{ .green = true };\n" +
            "    const p: Pair(u8, u16) = .{ .x = 2, .y = 300 };\n" +
            "    const f: u8 = if (!flags.red and flags.green and !flags.blue) 100 else 0;\n" +
            "    var total: u8 = sum(.{ .green = 3 }) + f + p.x + @as(u8, @intCast(p.y - 290));\n" +
            "    // @tagName of a comptime enum value, then @field by that name.\n" +
            "    const counts: FieldStruct(u8, null) = .{ .red = 1, .green = 2, .blue = 3 };\n" +
            "    inline for (0..3) |i| {\n" +
            "        const key = comptime keyFor(i);\n" +
            "        const tag = @tagName(key);\n" +
            "        total +%= @field(counts, tag) * @as(u8, @intCast(tag.len));\n" +
            "    }\n" +
            "    // A comptime struct parameter spelled in a generic method's return type.\n" +
            "    const b: Bits(5) = .{};\n" +
            "    var it = b.iter(.{ .step = 2 });\n" +
            "    return total +% it.next() +% b.mask;\n" +
            "}\n", 190, "" },
        // Task #94: the by-ref capture `if (opt) |*v|` points `v` INTO the optional: a write through it changes the
        // optional (a local, a struct field, and a missing payload taking the else arm); an optional pointer's capture
        // is the address of the pointer variable; an rvalue condition is a temporary the capture points into.
        new object[] { "if_capture_by_ref",
            "const Holder = struct {\n" +
            "    a: ?u8 = 5,\n" +
            "    b: ?u16 = null,\n" +
            "};\n" +
            "\n" +
            "fn maybe(n: u8) ?u8 {\n" +
            "    return if (n > 2) n else null;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var x: ?u8 = 10;\n" +
            "    if (x) |*v| {\n" +
            "        v.* += 3;\n" +
            "    }\n" +
            "    var h: Holder = .{};\n" +
            "    if (h.a) |*p| {\n" +
            "        p.* *= 2;\n" +
            "    } else {\n" +
            "        h.a = 99;\n" +
            "    }\n" +
            "    if (h.b) |*q| {\n" +
            "        q.* = 1;\n" +
            "    } else {\n" +
            "        h.b = 7;\n" +
            "    }\n" +
            "    var n: u8 = 4;\n" +
            "    var ptr: ?*u8 = &n;\n" +
            "    if (ptr) |*pp| {\n" +
            "        pp.*.* += 1;\n" +
            "        const same: *u8 = pp.*;\n" +
            "        same.* += 10;\n" +
            "    }\n" +
            "    var seen: u8 = 0;\n" +
            "    if (maybe(9)) |*m| {\n" +
            "        seen = m.*;\n" +
            "    }\n" +
            "    const got: u8 = if (ptr == null) 1 else 0;\n" +
            "    return x.? + h.a.? + @as(u8, @intCast(h.b.?)) + n + seen + got;\n" +
            "}\n", 54, "" },
        // Task #85: a pointer to a container TYPE as a comptime `anytype` argument (std.fmt.float.binaryToDecimal's
        // `comptime tables: anytype` fed `&Backend64_TablesFull`), directly or through a const bound to a comptime `if` of
        // type pointers; `x != if (c) a else b` as a comparison operand; a type body's `comptime check(...)` statement.
        new object[] { "comptime_type_pointer_namespace",
            "const Small = struct {\n" +
            "    const T = u64;\n" +
            "    const bound = 21;\n" +
            "    fn scale(i: u32) u32 {\n" +
            "        return i * 2;\n" +
            "    }\n" +
            "};\n" +
            "const Full = struct {\n" +
            "    const T = u64;\n" +
            "    const bound = 40;\n" +
            "    fn scale(i: u32) u32 {\n" +
            "        return i * 3;\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "fn use(comptime T: type, x: u32, comptime tables: anytype) u32 {\n" +
            "    if (T != tables.T) @compileError(\"table type mismatch\");\n" +
            "    return tables.scale(x) + tables.bound;\n" +
            "}\n" +
            "\n" +
            "fn check(ok: bool) void {\n" +
            "    if (!ok) unreachable;\n" +
            "}\n" +
            "\n" +
            "fn Checked(comptime n: u8) type {\n" +
            "    comptime check(n > 1);\n" +
            "    return struct {\n" +
            "        v: u8 = n,\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const small = true;\n" +
            "    const tables = if (!small) &Small else &Full;\n" +
            "    const t: u32 = use(u64, 3, tables) + use(u64, 1, &Small);\n" +
            "    const a: u8 = 3;\n" +
            "    const b: u8 = if (a != if (a > 2) @as(u8, 3) else 0) 7 else 9;\n" +
            "    const c: Checked(4) = .{};\n" +
            "    return @intCast(t + b + c.v);\n" +
            "}\n", 85, "" },
        // Task #96: an `if` statement whose then-arm is an assignment ended by the `else` (`if (c) i += 3 else i -= 1;`,
        // std.fmt.float.formatScientific); a nested `[3][2]u64` const read by row and element (one flat pinned table);
        // a string literal as a `catch` fallback for a slice payload.
        new object[] { "if_assign_arm_and_flat_table",
            "const TABLE: [3][2]u64 = .{\n" +
            "    .{ 1, 2 },\n" +
            "    .{ 3, 4 },\n" +
            "    .{ 5, 6 },\n" +
            "};\n" +
            "\n" +
            "fn row(i: u32) [2]u64 {\n" +
            "    return TABLE[i];\n" +
            "}\n" +
            "\n" +
            "fn pick(ok: bool, buf: []u8) error{Nope}![]u8 {\n" +
            "    if (!ok) return error.Nope;\n" +
            "    return buf[0..2];\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var i: u32 = 10;\n" +
            "    var j: u32 = 1;\n" +
            "    for (0..4) |k| {\n" +
            "        if (k % 2 == 0) i += 3 else i -= 1;\n" +
            "        if (k == 3) j = 7 else j *= 2;\n" +
            "        if (k > 5) j <<= 1 else j |= 1;\n" +
            "    }\n" +
            "    var buf = [_]u8{ 'a', 'b', 'c' };\n" +
            "    const good = pick(true, &buf) catch \"ERR\";\n" +
            "    const bad = pick(false, &buf) catch \"ERR\";\n" +
            "    const r = row(2);\n" +
            "    return @intCast(i + j + good.len * 10 + bad.len + r[0] * r[1] + TABLE[1][0]);\n" +
            "}\n", 77, "" },
        // Task #92: a plain function returning from a `comptime { }` block is legal where zig evaluates the call at
        // compile time (`comptime seven()`, a function only a comptime call reaches, a top-level initializer), in an
        // `inline fn`, and in a function nothing calls; a runtime call of one is rejected (unit-pinned).
        new object[] { "comptime_return_called_at_compile_time",
            "fn seven() u8 {\n" +
            "    comptime {\n" +
            "        return 7;\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "inline fn five() u8 {\n" +
            "    comptime {\n" +
            "        return 5;\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "fn viaHelper() u8 {\n" +
            "    return seven() + 1;\n" +
            "}\n" +
            "\n" +
            "fn neverCalled() u8 {\n" +
            "    return seven();\n" +
            "}\n" +
            "\n" +
            "const top = seven();\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const a = comptime seven();\n" +
            "    const b = comptime viaHelper();\n" +
            "    return a + b + top + five();\n" +
            "}\n", 27, "" },
        // Task #97: `~x` of a u8 / u16 keeps its type (no C integer promotion), and a complement or wrapping op of an
        // arbitrary-width unsigned (`u3`, `u5`) wraps at that width; std.math.rotl's `x << r | x >> 1 +% ~r` is the shape
        // that had silently lost the rotated-out bit.
        new object[] { "narrow_complement_and_wrap",
            "fn rotl8(x: u8, r: u3) u8 {\n" +
            "    return x << r | x >> 1 +% ~r;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var x: u8 = 1;\n" +
            "    x += 0;\n" +
            "    var h: u16 = 0x00f0;\n" +
            "    h += 0;\n" +
            "    var small: u5 = 3;\n" +
            "    small += 0;\n" +
            "    var total: u32 = 0;\n" +
            "    if (~x == 254) total += 1;\n" +
            "    if (~h == 0xff0f) total += 2;\n" +
            "    const ns: u5 = ~small;\n" +
            "    total += ns;\n" +
            "    const w: u5 = small -% 5;\n" +
            "    total += w;\n" +
            "    const m: u3 = @as(u3, 5) *% 3;\n" +
            "    total += m;\n" +
            "    total += rotl8(0b1000_0001, 1);\n" +
            "    total += rotl8(0b0100_0000, 3);\n" +
            "    return @intCast(total);\n" +
            "}\n", 73, "" },
        // Task #98: the legal forms of division: @divTrunc / @divFloor / @mod / @rem of a signed integer, unsigned `/` and
        // `%`, float `/`, a float @mod, and `/` over comptime-known signed operands. (A runtime signed `/` or `%` is rejected,
        // as in zig; unit-pinned.)
        new object[] { "division_builtins_and_unsigned",
            "pub fn main() u8 {\n" +
            "    var a: i32 = -7;\n" +
            "    a += 0;\n" +
            "    var u: u32 = 17;\n" +
            "    u += 0;\n" +
            "    var f: f32 = 9.0;\n" +
            "    f += 0;\n" +
            "    const k: i32 = -9;\n" +
            "    var total: i32 = 0;\n" +
            "    total += @divTrunc(a, 2);\n" +
            "    total += @divFloor(a, 2);\n" +
            "    total += @mod(a, 3);\n" +
            "    total += @rem(a, 3);\n" +
            "    total += @intCast(u / 4 + u % 5);\n" +
            "    total += @intFromFloat(f / 2.0);\n" +
            "    total += @intFromFloat(@mod(f, 4.0));\n" +
            "    total += k / 3;\n" +
            "    total += -12 / 4;\n" +
            "    return @intCast(total + 20);\n" +
            "}\n", 19, "" },
        // Task #99: a comptime FUNCTION argument to a type-returning generic (std.StaticStringMapWithEql's
        // `comptime eql: fn (a: []const u8, b: []const u8) bool`): one reified struct per function, each method calling
        // its own; and a tuple's `.len`.
        new object[] { "type_returning_comptime_fn_arg",
            "fn sameLen(a: []const u8, b: []const u8) bool {\n" +
            "    return a.len == b.len;\n" +
            "}\n" +
            "\n" +
            "fn exact(a: []const u8, b: []const u8) bool {\n" +
            "    if (a.len != b.len) return false;\n" +
            "    for (a, b) |x, y| {\n" +
            "        if (x != y) return false;\n" +
            "    }\n" +
            "    return true;\n" +
            "}\n" +
            "\n" +
            "fn Matcher(comptime V: type, comptime eql: fn (a: []const u8, b: []const u8) bool) type {\n" +
            "    return struct {\n" +
            "        key: []const u8,\n" +
            "        val: V,\n" +
            "\n" +
            "        fn get(self: @This(), k: []const u8) ?V {\n" +
            "            return if (eql(self.key, k)) self.val else null;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "fn count(comptime t: anytype) usize {\n" +
            "    return t.len;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const loose: Matcher(u8, sameLen) = .{ .key = \"abc\", .val = 7 };\n" +
            "    const strict: Matcher(u8, exact) = .{ .key = \"abc\", .val = 9 };\n" +
            "    var total: usize = 0;\n" +
            "    total += loose.get(\"xyz\") orelse 0;\n" +
            "    total += strict.get(\"xyz\") orelse 100;\n" +
            "    total += strict.get(\"abc\") orelse 0;\n" +
            "    total += count(.{ 1, 2, 3 }) * 10;\n" +
            "    return @intCast(total);\n" +
            "}\n", 146, "" },
        // Task #101: `@clz` / `@ctz` of arbitrary-width integers (u3, u5, u12) count within the declared width, zero and
        // comptime-folded operands included (the carrier byte's count had been used).
        new object[] { "clz_ctz_arbitrary_width",
            "pub fn main() u8 {\n" +
            "    var z: u5 = 5;\n" +
            "    z += 0;\n" +
            "    var q: u12 = 0x0f0;\n" +
            "    q += 0;\n" +
            "    var s: u3 = 1;\n" +
            "    s += 0;\n" +
            "    var zero: u5 = 0;\n" +
            "    zero += 0;\n" +
            "    var total: u32 = 0;\n" +
            "    total += @clz(z);\n" +
            "    total += @as(u32, @clz(q)) * 10;\n" +
            "    total += @as(u32, @clz(s)) * 100;\n" +
            "    total += @ctz(zero);\n" +
            "    total += @clz(zero);\n" +
            "    total += @clz(@as(u5, 3));\n" +
            "    return @intCast(total % 256);\n" +
            "}\n", 255, "" },
        // A `@clz` / `@ctz` / `@popCount` count is an unsigned Log2IntCeil(T) (task #102): a `u3`'s count is a `u2`,
        // so `@clz(s) + 1` fits and `@popCount(s) * 3` is fine. zig returns 33.
        new object[] { "clz_count_is_log2_int_ceil",
            "pub fn main() u8 {\n" +
            "    var s: u3 = 1;\n" +
            "    _ = &s;\n" +
            "    const r = @clz(s) + 1;\n" +
            "    const q = @popCount(s) * 3;\n" +
            "    return @as(u8, r) * 10 + q;\n" +
            "}\n", 33, "" },
        // A type-returning generic's comptime function argument passed along by a nested container's field, a value
        // switch block prong that always returns, and unsigned `%` over promoted `u16` operands (task #103, std.PriorityQueue's
        // shapes). zig returns 118.
        new object[] { "comptime_fn_arg_nested_container",
            "const E = error{ Full, Empty };\n" +
            "\n" +
            "fn less(_: void, a: u16, b: u16) bool {\n" +
            "    return a < b;\n" +
            "}\n" +
            "\n" +
            "fn Heap(comptime T: type, comptime Context: type, comptime lessFn: fn (context: Context, a: T, b: T) bool) type {\n" +
            "    return struct {\n" +
            "        items: [8]T = undefined,\n" +
            "        len: usize = 0,\n" +
            "        context: Context = undefined,\n" +
            "        const Self = @This();\n" +
            "        pub const Cursor = struct {\n" +
            "            heap: *Heap(T, Context, lessFn),\n" +
            "            at: usize,\n" +
            "            pub fn next(c: *Cursor) ?T {\n" +
            "                if (c.at >= c.heap.len) return null;\n" +
            "                c.at += 1;\n" +
            "                return c.heap.items[c.at - 1];\n" +
            "            }\n" +
            "        };\n" +
            "        fn put(self: *Self, v: T) E!void {\n" +
            "            if (self.len == self.items.len) return error.Full;\n" +
            "            var i = self.len;\n" +
            "            self.items[i] = v;\n" +
            "            self.len += 1;\n" +
            "            while (i > 0 and lessFn(self.context, self.items[i], self.items[i - 1])) : (i -= 1) {\n" +
            "                const t = self.items[i];\n" +
            "                self.items[i] = self.items[i - 1];\n" +
            "                self.items[i - 1] = t;\n" +
            "            }\n" +
            "        }\n" +
            "        fn cursor(self: *Self) Cursor {\n" +
            "            return .{ .heap = self, .at = 0 };\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "fn fill(h: *Heap(u16, void, less), n: u16) u16 {\n" +
            "    var i: u16 = 0;\n" +
            "    while (i < n) : (i += 1) {\n" +
            "        h.put((i * 37) % 101) catch |e| switch (e) {\n" +
            "            error.Full => {\n" +
            "                return i;\n" +
            "            },\n" +
            "            error.Empty => unreachable,\n" +
            "        };\n" +
            "    }\n" +
            "    return n;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var h: Heap(u16, void, less) = .{};\n" +
            "    const stored = fill(&h, 20);\n" +
            "    var c = h.cursor();\n" +
            "    var sum: u32 = 0;\n" +
            "    var prev: u16 = 0;\n" +
            "    var sorted = true;\n" +
            "    while (c.next()) |v| {\n" +
            "        if (v < prev) sorted = false;\n" +
            "        prev = v;\n" +
            "        sum += v;\n" +
            "    }\n" +
            "    if (!sorted) return 1;\n" +
            "    return @intCast(stored * 10 + sum % 97);\n" +
            "}\n", 118, "" },
        // A capture-`while` continue expression that reads the capture (`while (it) |n| : (it = n.next)`, task #105), over a
        // pointer optional and a value optional, with a labeled `continue` and a plain one. zig returns 10.
        new object[] { "while_capture_cont_reads_capture",
            "const Node = struct {\n" +
            "    v: u32,\n" +
            "    next: ?*Node = null,\n" +
            "};\n" +
            "\n" +
            "fn step(x: u8) ?u8 {\n" +
            "    return if (x >= 40) null else x + 7;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var c = Node{ .v = 9 };\n" +
            "    var b = Node{ .v = 5, .next = &c };\n" +
            "    var a = Node{ .v = 3, .next = &b };\n" +
            "    var sum: u32 = 0;\n" +
            "    var it: ?*Node = &a;\n" +
            "    while (it) |n| : (it = n.next) {\n" +
            "        sum = sum * 10 + n.v;\n" +
            "    }\n" +
            "    var hops: u32 = 0;\n" +
            "    var cur: ?u8 = 1;\n" +
            "    outer: while (cur) |x| : (cur = step(x)) {\n" +
            "        hops += 1;\n" +
            "        if (x % 2 == 0) continue :outer;\n" +
            "        hops += 100;\n" +
            "    }\n" +
            "    var skipped: u32 = 0;\n" +
            "    var it2: ?*Node = &a;\n" +
            "    while (it2) |n| : (it2 = n.next) {\n" +
            "        if (n.v == 5) continue;\n" +
            "        skipped += n.v;\n" +
            "    }\n" +
            "    return @intCast((sum + hops + skipped) % 256);\n" +
            "}\n", 10, "" },
        // A `comptime store: bool` argument to a type-returning generic that is a comptime question (`!cheap(K)` where cheap
        // switches over `@typeInfo(K)`, task #106, std.array_hash_map.Auto's shape). zig returns 108.
        new object[] { "comptime_bool_question_arg",
            "fn cheap(comptime K: type) bool {\n" +
            "    return switch (@typeInfo(K)) {\n" +
            "        .int, .bool => true,\n" +
            "        else => false,\n" +
            "    };\n" +
            "}\n" +
            "fn Box(comptime K: type, comptime store: bool) type {\n" +
            "    return struct {\n" +
            "        k: K,\n" +
            "        extra: Extra,\n" +
            "        pub const Extra = if (store) u32 else u8;\n" +
            "    };\n" +
            "}\n" +
            "fn Auto(comptime K: type) type {\n" +
            "    return Box(K, !cheap(K));\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const b: Auto(u16) = .{ .k = 3, .extra = 4 };\n" +
            "    const c: Box(u16, cheap(u16)) = .{ .k = 5, .extra = 6 };\n" +
            "    return @intCast(b.k + b.extra + @sizeOf(@TypeOf(b.extra)) * 10 + c.k + c.extra + @sizeOf(@TypeOf(c.extra)) * 20);\n" +
            "}\n", 108, "" },
        // Inline `union(enum) { … }` / `union { … }` types (task #104) as a parameter, a struct field (one holding an inline
        // struct payload) and a local annotation, switched over with capture prongs. zig returns 76.
        new object[] { "inline_union_types",
            "fn weigh(x: union(enum) { small: u8, big: u16, none }) u16 {\n" +
            "    return switch (x) {\n" +
            "        .small => |s| s,\n" +
            "        .big => |b| b * 2,\n" +
            "        .none => 1,\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "const Slot = struct {\n" +
            "    tag: union(enum) { n: u8, pair: struct { a: u8, b: u8 } },\n" +
            "    raw: union { word: u16, bytes: [2]u8 },\n" +
            "};\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var total: u16 = weigh(.{ .small = 7 }) + weigh(.{ .big = 20 }) + weigh(.none);\n" +
            "    const s = Slot{ .tag = .{ .pair = .{ .a = 3, .b = 4 } }, .raw = .{ .word = 0x0102 } };\n" +
            "    const extra: u16 = switch (s.tag) {\n" +
            "        .n => |v| v,\n" +
            "        .pair => |p| p.a * p.b,\n" +
            "    };\n" +
            "    total += extra;\n" +
            "    var local: union(enum) { on: u8, off } = .off;\n" +
            "    switch (local) {\n" +
            "        .off => {\n" +
            "            total += 5;\n" +
            "        },\n" +
            "        .on => {},\n" +
            "    }\n" +
            "    local = .{ .on = 9 };\n" +
            "    switch (local) {\n" +
            "        .on => |v| {\n" +
            "            total += v;\n" +
            "        },\n" +
            "        .off => {},\n" +
            "    }\n" +
            "    total += s.raw.word & 0xff;\n" +
            "    return @intCast(total);\n" +
            "}\n", 76, "" },
        // Tagged-union switch forms (task #109): assignment and capture-assignment prongs, `u == .tag` / `.tag == u` /
        // `u != .tag`, and a capture switch after `+=`, over a named and an inline union. zig returns 193.
        new object[] { "union_switch_assign_and_tag_compare",
            "const Cmd = union(enum) {\n" +
            "    add: u16,\n" +
            "    mul: u16,\n" +
            "    reset,\n" +
            "};\n" +
            "\n" +
            "fn apply(acc: *u16, c: Cmd) void {\n" +
            "    switch (c) {\n" +
            "        .add => |n| acc.* += n,\n" +
            "        .mul => |n| acc.* *= n,\n" +
            "        .reset => acc.* = 1,\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var acc: u16 = 1;\n" +
            "    const cmds = [_]Cmd{ .{ .add = 4 }, .{ .mul = 3 }, .reset, .{ .add = 9 }, .{ .mul = 2 } };\n" +
            "    var resets: u8 = 0;\n" +
            "    for (cmds) |c| {\n" +
            "        apply(&acc, c);\n" +
            "        if (c == .reset) resets += 1;\n" +
            "        if (.add == c) acc += 0;\n" +
            "    }\n" +
            "    var bonus: u16 = 0;\n" +
            "    for (cmds) |c| {\n" +
            "        bonus += switch (c) {\n" +
            "            .add => |n| n,\n" +
            "            .mul => |n| n * 10,\n" +
            "            .reset => 100,\n" +
            "        };\n" +
            "    }\n" +
            "    var st: union(enum) { idle, busy: u8 } = .{ .busy = 7 };\n" +
            "    var seen: u16 = 0;\n" +
            "    switch (st) {\n" +
            "        .idle => seen = 1,\n" +
            "        .busy => |b| seen += b,\n" +
            "    }\n" +
            "    st = .idle;\n" +
            "    if (st != .busy) seen += 2;\n" +
            "    return @intCast((acc + bonus + seen + resets) % 256);\n" +
            "}\n", 193, "" },
        // Inline container types as call arguments (task #110): `@as(struct {…}, …)` with a field default, `@as(enum {…}, .z)`,
        // `@as(union(enum) {…}, …)`, `@TypeOf(@as(union {…}, …))` and a struct type passed to an `anytype` parameter. zig returns 62.
        new object[] { "inline_container_type_args",
            "fn area(s: anytype) u32 {\n" +
            "    return @as(u32, s.w) * s.h;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const v = @as(struct { a: u8, b: u8 = 2 }, .{ .a = 4 });\n" +
            "    const e = @as(enum { x, y, z }, .z);\n" +
            "    const u = @as(union(enum) { small: u8, big: u16 }, .{ .big = 30 });\n" +
            "    const T = @TypeOf(@as(union { p: u8, q: u16 }, .{ .p = 1 }));\n" +
            "    const t: T = .{ .p = 9 };\n" +
            "    const big: u16 = switch (u) {\n" +
            "        .small => |s| s,\n" +
            "        .big => |b| b,\n" +
            "    };\n" +
            "    const r = area(@as(struct { w: u8, h: u32 }, .{ .w = 3, .h = 5 }));\n" +
            "    return @intCast(v.a + v.b + @intFromEnum(e) + big + t.p + r);\n" +
            "}\n", 62, "" },
        // In-function enum / union declarations (task #111): `enum(u8)`, `union(enum)` with an enum payload, an untagged
        // `union`, and a same-named local enum in a second function; unsigned `%` over two call results. zig returns 166.
        new object[] { "local_enum_and_union_decls",
            "fn score() u16 {\n" +
            "    const Suit = enum(u8) { clubs = 1, hearts = 3, spades = 7 };\n" +
            "    const Card = union(enum) { pip: u8, face: Suit, joker };\n" +
            "    const Raw = union { word: u16, half: u8 };\n" +
            "    const hand = [_]Card{ .{ .pip = 9 }, .{ .face = .hearts }, .joker, .{ .face = .spades } };\n" +
            "    var total: u16 = 0;\n" +
            "    for (hand) |c| {\n" +
            "        total += switch (c) {\n" +
            "            .pip => |p| p,\n" +
            "            .face => |s| @as(u16, @intFromEnum(s)) * 10,\n" +
            "            .joker => 50,\n" +
            "        };\n" +
            "    }\n" +
            "    const r = Raw{ .word = 5 };\n" +
            "    return total + r.word;\n" +
            "}\n" +
            "\n" +
            "fn other() u8 {\n" +
            "    const Suit = enum { a, b, c };\n" +
            "    return @intFromEnum(Suit.c);\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    return @intCast((score() + other()) % 256);\n" +
            "}\n", 166, "" },
        // A container type as a switch prong's value (task #108, std.MultiArrayList's `Elem`): struct and enum prongs selected by a
        // comptime type switch, an unselected capture prong holding a struct with a const member. zig returns 45.
        new object[] { "container_type_prong_value",
            "fn Pick(comptime T: type) type {\n" +
            "    return switch (@typeInfo(T)) {\n" +
            "        .int => struct { lo: T, hi: T },\n" +
            "        .@\"union\" => |u| struct {\n" +
            "            pub const layout = u.layout;\n" +
            "            tag: u8,\n" +
            "        },\n" +
            "        .@\"enum\" => enum { first, second },\n" +
            "        else => T,\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const p: Pick(u16) = .{ .lo = 3, .hi = 40 };\n" +
            "    const q: Pick(bool) = true;\n" +
            "    const e: Pick(enum { a }) = .second;\n" +
            "    return @intCast(p.lo + p.hi + @intFromBool(q) + @intFromEnum(e));\n" +
            "}\n", 45, "" },
        // The general multi-object `for` (task #108, std.MultiArrayList's shapes): a `*` capture mid-triple, a `0..` object last,
        // four objects with three pointer captures, a bounded range object, and zig fmt's trailing comma. zig returns 130.
        new object[] { "multi_object_for_shapes",
            "pub fn main() u8 {\n" +
            "    const src = [_]u8{ 1, 2, 3, 4 };\n" +
            "    var dst: [4]u8 = undefined;\n" +
            "    const scale = [_]u8{ 10, 20, 30, 40 };\n" +
            "    for (src, &dst, scale) |s, *d, k| {\n" +
            "        d.* = s + k;\n" +
            "    }\n" +
            "    var weighted: u32 = 0;\n" +
            "    for (dst, scale, 0..) |d, k, i| {\n" +
            "        weighted += @as(u32, d) * @as(u32, @intCast(i)) + k;\n" +
            "    }\n" +
            "    var a: [3]u8 = .{ 0, 0, 0 };\n" +
            "    var b: [3]u8 = .{ 0, 0, 0 };\n" +
            "    var c: [3]u8 = .{ 0, 0, 0 };\n" +
            "    const order = [_]u8{ 2, 0, 1 };\n" +
            "    for (order, &a, &b, &c) |o, *x, *y, *z| {\n" +
            "        x.* = o;\n" +
            "        y.* = o * 2;\n" +
            "        z.* = o * 3;\n" +
            "    }\n" +
            "    var ranged: u32 = 0;\n" +
            "    for (5..8, order) |r, o| {\n" +
            "        ranged += @as(u32, @intCast(r)) * o;\n" +
            "    }\n" +
            "    var trailing: u32 = 0;\n" +
            "    for (\n" +
            "        src,\n" +
            "        scale,\n" +
            "    ) |s, k| {\n" +
            "        trailing += s * k;\n" +
            "    }\n" +
            "    const sum = weighted + a[0] + b[1] + c[2] + ranged + trailing;\n" +
            "    return @intCast(sum % 256);\n" +
            "}\n", 130, "" },
        // A generic instance's value consts sizing its nested container's fields (task #108, std.MultiArrayList's `Slice`):
        // `ptrs: [names.len]u16` and `bytes: [width]u8` over the instance's consts. zig returns 46.
        new object[] { "nested_container_reads_instance_consts",
            "fn Columns(comptime T: type) type {\n" +
            "    return struct {\n" +
            "        rows: u8 = 0,\n" +
            "        const Self = @This();\n" +
            "        const width = @sizeOf(T);\n" +
            "        const names = [_][]const u8{ \"lo\", \"mid\", \"hi\" };\n" +
            "        pub const Slice = struct {\n" +
            "            ptrs: [names.len]u16,\n" +
            "            bytes: [width]u8,\n" +
            "            pub fn total(s: Slice) u32 {\n" +
            "                var t: u32 = 0;\n" +
            "                for (s.ptrs) |p| t += p;\n" +
            "                return t + @as(u32, @intCast(s.bytes.len));\n" +
            "            }\n" +
            "        };\n" +
            "        fn slice(self: Self) Slice {\n" +
            "            var s: Slice = undefined;\n" +
            "            for (&s.ptrs, 0..) |*p, i| p.* = @intCast(i * 10 + self.rows);\n" +
            "            for (&s.bytes) |*b| b.* = 0;\n" +
            "            return s;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const c: Columns(u32) = .{ .rows = 4 };\n" +
            "    const s = c.slice();\n" +
            "    return @intCast(s.total());\n" +
            "}\n", 46, "" },
        // `for … else` statements (task #108, std.meta.FieldEnum's shape): a break skipping the else, an else that returns, a
        // break inside the else reaching an outer loop, a one-statement else. zig returns 169.
        new object[] { "for_else_statements",
            "fn find(xs: []const u8, want: u8) u8 {\n" +
            "    for (xs, 0..) |x, i| {\n" +
            "        if (x == want) break;\n" +
            "        _ = i;\n" +
            "    } else {\n" +
            "        return 100;\n" +
            "    }\n" +
            "    return 1;\n" +
            "}\n" +
            "\n" +
            "fn firstGap(xs: []const u8) u8 {\n" +
            "    for (xs, 0..) |x, i| {\n" +
            "        if (x != i) return @intCast(i);\n" +
            "    } else {\n" +
            "        return 50;\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const xs = [_]u8{ 0, 1, 2, 7 };\n" +
            "    var total: u32 = 0;\n" +
            "    total += find(&xs, 2);\n" +
            "    total += find(&xs, 9);\n" +
            "    total += firstGap(&xs);\n" +
            "    total += firstGap(xs[0..3]);\n" +
            "    var outer: u32 = 0;\n" +
            "    while (outer < 5) : (outer += 1) {\n" +
            "        for (xs) |x| {\n" +
            "            if (x == 99) break;\n" +
            "        } else {\n" +
            "            if (outer == 2) break;\n" +
            "        }\n" +
            "    }\n" +
            "    total += outer;\n" +
            "    var hits: u32 = 0;\n" +
            "    for (xs) |x| {\n" +
            "        if (x > 5) continue;\n" +
            "        hits += 1;\n" +
            "    } else hits += 10;\n" +
            "    return @intCast(total + hits);\n" +
            "}\n", 169, "" },
        // `@Enum` as a type-returning function's result (task #108, std.meta.FieldEnum's shape): spelled names and values, a
        // non-exhaustive mode, a value from a comptime param, and typed saturating ops folded at comptime. zig returns 37.
        new object[] { "enum_builtin_and_saturating_fold",
            "fn Flags(comptime width: u8) type {\n" +
            "    return @Enum(u8, .exhaustive, &.{ \"read\", \"write\", \"exec\" }, &.{ 1, 2, width });\n" +
            "}\n" +
            "\n" +
            "fn Level(comptime n: usize) type {\n" +
            "    return @Enum(u4, .nonexhaustive, &.{ \"low\", \"high\" }, &.{ 0, n -| 1 });\n" +
            "}\n" +
            "\n" +
            "fn describe(f: Flags(4)) u8 {\n" +
            "    return switch (f) {\n" +
            "        .read => 10,\n" +
            "        .write => 20,\n" +
            "        .exec => 40,\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const F = Flags(4);\n" +
            "    const x: F = .exec;\n" +
            "    const L = Level(9);\n" +
            "    const h: L = .high;\n" +
            "    const sat: u8 = @as(u8, 3) -| 5;\n" +
            "    const big: u8 = @as(u8, 250) +| 10;\n" +
            "    return @intFromEnum(x) + describe(.write) + @as(u8, @intFromEnum(h)) + sat + (big - 250);\n" +
            "}\n", 37, "" },
        // A struct returned by a function's comptime block (task #100, step 1): an early return, a pointer to a comptime array
        // and a pointer to a comptime struct literal, read after deep calls that would overwrite a dangling stack frame. zig returns 195.
        new object[] { "comptime_block_struct_pinned",
            "const Meta = struct {\n" +
            "    count: u32,\n" +
            "    first: [*]const u32,\n" +
            "};\n" +
            "\n" +
            "const Table = struct {\n" +
            "    vals: [*]const u32,\n" +
            "    len: u32,\n" +
            "    peak: u32 = 0,\n" +
            "    meta: *const Meta = &empty_meta,\n" +
            "\n" +
            "    const empty_vals = [0]u32{};\n" +
            "    const empty_meta = Meta{ .count = 0, .first = &empty_vals };\n" +
            "\n" +
            "    inline fn build(comptime n: u32) Table {\n" +
            "        comptime {\n" +
            "            var self = Table{ .vals = &empty_vals, .len = n };\n" +
            "            if (n == 0) return self;\n" +
            "            var arr: [n]u32 = undefined;\n" +
            "            for (&arr, 0..) |*e, i| {\n" +
            "                e.* = @intCast(i * i);\n" +
            "                self.peak = @max(self.peak, e.*);\n" +
            "            }\n" +
            "            const fin = arr;\n" +
            "            self.vals = &fin;\n" +
            "            self.meta = &.{ .count = n * 10, .first = &fin };\n" +
            "            return self;\n" +
            "        }\n" +
            "    }\n" +
            "\n" +
            "    fn at(t: Table, i: u32) u32 {\n" +
            "        return t.vals[i];\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "fn churn(depth: u32) u32 {\n" +
            "    var buf: [64]u32 = undefined;\n" +
            "    for (&buf, 0..) |*b, i| b.* = @intCast(i + depth);\n" +
            "    return if (depth == 0) buf[63] else churn(depth - 1) + buf[0];\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const t = Table.build(5);\n" +
            "    const e = Table.build(0);\n" +
            "    // Deep calls overwrite the stack a dangling pointer into build's frame would still read.\n" +
            "    const noise = churn(8);\n" +
            "    return @intCast((t.at(3) + t.len + t.peak + e.len + t.meta.count + t.meta.first[4] + e.meta.count + noise) % 256);\n" +
            "}\n", 195, "" },
        // A comptime block run one statement at a time (task #100, step 2): an array sized by what the block computed so far and a
        // for over a tuple of pairs read by position, the StaticStringMap.initComptime shapes. zig returns 45.
        new object[] { "comptime_block_session",
            "const Hist = struct {\n" +
            "    counts: [*]const u8,\n" +
            "    len: u32,\n" +
            "    peak: u32 = 0,\n" +
            "    label_len: u32 = 0,\n" +
            "\n" +
            "    inline fn build(comptime n: u32) Hist {\n" +
            "        comptime {\n" +
            "            var self = Hist{ .counts = undefined, .len = 0 };\n" +
            "            for (0..n) |i| self.peak = @max(self.peak, @as(u32, @intCast(i * 3 % 7)));\n" +
            "            // Sized by what the block computed so far.\n" +
            "            var bins: [self.peak + 1]u8 = undefined;\n" +
            "            for (&bins, 0..) |*b, i| b.* = @intCast(i + 1);\n" +
            "            const fin = bins;\n" +
            "            self.counts = &fin;\n" +
            "            self.len = self.peak + 1;\n" +
            "            const labels = .{ .{ \"ab\", 1 }, .{ \"cde\", 2 }, .{ \"f\", 4 } };\n" +
            "            for (labels, 0..) |l, i| self.label_len += @as(u32, l.@\"0\".len) * l.@\"1\" + @as(u32, @intCast(i));\n" +
            "            return self;\n" +
            "        }\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const h = Hist.build(6);\n" +
            "    const pair = .{ \"xyz\", 7 };\n" +
            "    return @intCast(h.counts[h.len - 1] + h.len + h.peak + h.label_len + pair.@\"0\".len + pair.@\"1\");\n" +
            "}\n", 45, "" },
        // A comptime anytype tuple argument (task #100, step 3): each table keys its own instance, a helper iterates the tuple from the
        // comptime block only, and a ?Entry result takes a bare .{ ... } payload; the std.StaticStringMap.initComptime shapes. zig returns 58.
        new object[] { "comptime_anytype_tuple_arg",
            "const Entry = struct {\n" +
            "    key: []const u8,\n" +
            "    value: u8,\n" +
            "};\n" +
            "\n" +
            "const Table = struct {\n" +
            "    keys: [*]const []const u8,\n" +
            "    values: [*]const u8,\n" +
            "    len: u32,\n" +
            "    longest: u32 = 0,\n" +
            "\n" +
            "    inline fn init(comptime pairs: anytype) Table {\n" +
            "        comptime {\n" +
            "            var keys: [pairs.len][]const u8 = undefined;\n" +
            "            var values: [pairs.len]u8 = undefined;\n" +
            "            var self = Table{ .keys = undefined, .values = undefined, .len = pairs.len };\n" +
            "            fill(&self, pairs, &keys, &values);\n" +
            "            const fin_keys = keys;\n" +
            "            const fin_values = values;\n" +
            "            self.keys = &fin_keys;\n" +
            "            self.values = &fin_values;\n" +
            "            return self;\n" +
            "        }\n" +
            "    }\n" +
            "\n" +
            "    fn fill(self: *Table, pairs: anytype, keys: [][]const u8, values: []u8) void {\n" +
            "        for (pairs, 0..) |kv, i| {\n" +
            "            keys[i] = kv.@\"0\";\n" +
            "            values[i] = kv.@\"1\";\n" +
            "            self.longest = @max(self.longest, @as(u32, @intCast(kv.@\"0\".len)));\n" +
            "        }\n" +
            "    }\n" +
            "\n" +
            "    fn find(t: Table, key: []const u8) ?Entry {\n" +
            "        var i: u32 = 0;\n" +
            "        while (i < t.len) : (i += 1) {\n" +
            "            const k = t.keys[i];\n" +
            "            if (k.len != key.len) continue;\n" +
            "            var j: usize = 0;\n" +
            "            while (j < k.len and k[j] == key[j]) : (j += 1) {}\n" +
            "            if (j == k.len) return .{ .key = k, .value = t.values[i] };\n" +
            "        }\n" +
            "        return null;\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "const small = Table.init(.{ .{ \"one\", 1 }, .{ \"three\", 3 }, .{ \"ten\", 10 } });\n" +
            "const other = Table.init(.{ .{ \"seven\", 7 }, .{ \"forty\", 40 } });\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var total: u32 = small.longest * 100 + other.len;\n" +
            "    if (small.find(\"three\")) |e| total += e.value + @as(u32, @intCast(e.key.len));\n" +
            "    if (other.find(\"forty\")) |e| total += e.value;\n" +
            "    if (small.find(\"four\") == null) total += 20;\n" +
            "    return @intCast(total % 256);\n" +
            "}\n", 58, "" },
        // A global array literal whose address is taken (task #115): pinned for the program's life, as a struct field and as a
        // slice const. zig returns 12.
        new object[] { "global_array_literal_address",
            "const P = struct { vals: []const u8 };\n" +
            "const nums = P{ .vals = &[_]u8{ 7, 9 } };\n" +
            "const direct: []const u8 = &[_]u8{ 1, 2, 3 };\n" +
            "pub fn main() u8 {\n" +
            "    return nums.vals[1] + direct[2];\n" +
            "}\n", 12, "" },
        // void as data (task #114): [N]void, []const void, [*]const void, ?void through a generic map, and {} as a list element;
        // the std.StaticStringMap(void) shapes. zig returns 86.
        new object[] { "void_as_data",
            "fn Map(comptime V: type) type {\n" +
            "    return struct {\n" +
            "        keys: []const u8,\n" +
            "        vals: []const V,\n" +
            "\n" +
            "        const Self = @This();\n" +
            "\n" +
            "        fn get(self: Self, k: u8) ?V {\n" +
            "            for (self.keys, 0..) |key, i| {\n" +
            "                if (key == k) return self.vals[i];\n" +
            "            }\n" +
            "            return null;\n" +
            "        }\n" +
            "\n" +
            "        fn has(self: Self, k: u8) bool {\n" +
            "            return self.get(k) != null;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "const unit_vals = [3]void{ {}, {}, {} };\n" +
            "const set = Map(void){ .keys = \"abc\", .vals = &unit_vals };\n" +
            "const nums = Map(u8){ .keys = \"xy\", .vals = &[_]u8{ 7, 9 } };\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var slots: [4]void = undefined;\n" +
            "    slots[2] = {};\n" +
            "    const view: []const void = &slots;\n" +
            "    var many: [*]const void = &unit_vals;\n" +
            "    many += 1;\n" +
            "    _ = many[0];\n" +
            "    var total: u32 = @intCast(view.len + unit_vals.len);\n" +
            "    if (set.has('b')) total += 10;\n" +
            "    if (!set.has('z')) total += 20;\n" +
            "    if (set.get('c')) |_| total += 40;\n" +
            "    if (nums.get('y')) |n| total += n;\n" +
            "    return @intCast(total);\n" +
            "}\n", 86, "" },
        // An enum literal with no result type (task #113): tuples of enum literals iterated by a comptime-only helper (two tables,
        // two instances), a const literal coerced to an enum, an optional enum and a tagged union; the StaticStringMap(Kw) shapes. zig returns 163.
        new object[] { "enum_literal_no_result_type",
            "const Color = enum(u8) { red = 1, green = 2, blue = 4 };\n" +
            "const Shape = union(enum) { none, circle: u8 };\n" +
            "\n" +
            "const Table = struct {\n" +
            "    keys: [*]const u8,\n" +
            "    colors: [*]const Color,\n" +
            "    len: u32,\n" +
            "\n" +
            "    inline fn init(comptime pairs: anytype) Table {\n" +
            "        comptime {\n" +
            "            var keys: [pairs.len]u8 = undefined;\n" +
            "            var colors: [pairs.len]Color = undefined;\n" +
            "            fill(pairs, &keys, &colors);\n" +
            "            const fin_keys = keys;\n" +
            "            const fin_colors = colors;\n" +
            "            return .{ .keys = &fin_keys, .colors = &fin_colors, .len = pairs.len };\n" +
            "        }\n" +
            "    }\n" +
            "\n" +
            "    fn fill(pairs: anytype, keys: []u8, colors: []Color) void {\n" +
            "        for (pairs, 0..) |kv, i| {\n" +
            "            keys[i] = kv.@\"0\";\n" +
            "            colors[i] = kv.@\"1\";\n" +
            "        }\n" +
            "    }\n" +
            "\n" +
            "    fn find(t: Table, key: u8) ?Color {\n" +
            "        var i: u32 = 0;\n" +
            "        while (i < t.len) : (i += 1) {\n" +
            "            if (t.keys[i] == key) return t.colors[i];\n" +
            "        }\n" +
            "        return null;\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "const warm = Table.init(.{ .{ 'r', .red }, .{ 'g', .green } });\n" +
            "const cool = Table.init(.{ .{ 'r', .blue }, .{ 'g', .green } });\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const lit = .blue;\n" +
            "    const c: Color = lit;\n" +
            "    const maybe: ?Color = lit;\n" +
            "    const none = .none;\n" +
            "    const s: Shape = none;\n" +
            "    var total: u8 = @intFromEnum(c) + @intFromEnum(maybe.?);\n" +
            "    if (s == .none) total += 10;\n" +
            "    if (warm.find('r')) |w| total += @intFromEnum(w) * 16;\n" +
            "    if (cool.find('r')) |w| total += @intFromEnum(w) * 32;\n" +
            "    if (warm.find('x') == null) total += 1;\n" +
            "    return total;\n" +
            "}\n", 163, "" },
        // comptime f() catch unreachable as a comptime value (task #117): the std.Random.int shape, an @Int width from a
        // folded error-union call. zig returns 108.
        new object[] { "comptime_catch_unreachable_width",
            "fn divCeil(comptime T: type, a: T, b: T) !T {\n" +
            "    if (b == 0) return error.DivisionByZero;\n" +
            "    return (a + b - 1) / b;\n" +
            "}\n" +
            "\n" +
            "fn widen(comptime T: type, x: T) u64 {\n" +
            "    const bits = @typeInfo(T).int.bits;\n" +
            "    const ceil_bytes = comptime divCeil(u16, bits, 8) catch unreachable;\n" +
            "    const Wide = @Int(.unsigned, ceil_bytes * 8);\n" +
            "    const w: Wide = x;\n" +
            "    return @as(u64, w) + ceil_bytes;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    return @intCast(widen(u12, 100) + widen(u3, 5));\n" +
            "}\n", 108, "" },
        // Slice fields through a single pointer to a slice (task #118): e.key_ptr.len as std.StringHashMap's iterator entries
        // read it, and a write through the pointer. zig returns 211.
        new object[] { "slice_fields_through_pointer",
            "const Entry = struct { key_ptr: *const []const u8, weight: u8 };\n" +
            "\n" +
            "fn score(e: Entry) usize {\n" +
            "    return e.key_ptr.len * e.weight + e.key_ptr.ptr[0];\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const word: []const u8 = \"hey\";\n" +
            "    var other: []const u8 = \"ab\";\n" +
            "    const p = &other;\n" +
            "    p.len = 1;\n" +
            "    return @intCast(score(.{ .key_ptr = &word, .weight = 3 }) + other.len + p.ptr[0]);\n" +
            "}\n", 211, "" },
        // Format guards over a comptime string (task #121, step 1): std.Io.Writer.printValue's b64 / is_any guards settle, so the
        // guarded @compileError arms are never analysed; is_tuple; a struct field's declared width through anytype. zig returns 68.
        new object[] { "comptime_format_guards",
            "const ANY = \"any\";\n" +
            "\n" +
            "fn eql(a: []const u8, b: []const u8) bool {\n" +
            "    if (a.len != b.len) return false;\n" +
            "    for (a, b) |x, y| if (x != y) return false;\n" +
            "    return true;\n" +
            "}\n" +
            "\n" +
            "fn check(comptime fmt: []const u8) u8 {\n" +
            "    switch (fmt.len) {\n" +
            "        3 => if (fmt[0] == 'b' and fmt[1] == '6' and fmt[2] == '4') switch (fmt[0]) {\n" +
            "            'b' => return 1,\n" +
            "            else => @compileError(\"not b64: \" ++ fmt),\n" +
            "        },\n" +
            "        else => {},\n" +
            "    }\n" +
            "    const is_any = comptime eql(fmt, ANY);\n" +
            "    if (!is_any and fmt.len > 1) @compileError(\"bad format \" ++ fmt);\n" +
            "    return @as(u8, @intFromBool(is_any)) * 5 + 2;\n" +
            "}\n" +
            "\n" +
            "fn bitsOf(v: anytype) u16 {\n" +
            "    return @typeInfo(@TypeOf(v)).int.bits;\n" +
            "}\n" +
            "\n" +
            "const Rec = struct { wide: u21, narrow: u8 };\n" +
            "const Pair = struct { u8, u16 };\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const r = Rec{ .wide = 3, .narrow = 4 };\n" +
            "    const tuple_flags: u8 = (if (@typeInfo(Rec).@\"struct\".is_tuple) 100 else 0) + (if (@typeInfo(Pair).@\"struct\".is_tuple) 10 else 0);\n" +
            "    const bits: u16 = bitsOf(@field(r, \"wide\")) + bitsOf(r.narrow);\n" +
            "    return check(\"any\") + check(\"b64\") * 20 + check(\"x\") + tuple_flags + @as(u8, @intCast(bits));\n" +
            "}\n", 68, "" },
        // Task #119: `@typeInfo(@TypeOf(p)).pointer.size` of an anytype argument, from the pointer's spelling: `&x` and `*T`
        // are .one, `[*]T` and a slice's `.ptr` .many, `[*c]T` .c, a slice .slice; each class keys its own instance.
        new object[] { "pointer_size_class",
            "const S = struct { a: u8 };\n" +
            "\n" +
            "fn classify(p: anytype) u8 {\n" +
            "    const P = @TypeOf(p);\n" +
            "    return switch (@typeInfo(P).pointer.size) {\n" +
            "        .one => 1,\n" +
            "        .many => 2,\n" +
            "        .c => 3,\n" +
            "        .slice => 4,\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "fn isOne(p: anytype) u8 {\n" +
            "    return if (@typeInfo(@TypeOf(p)).pointer.size == .one) 10 else 20;\n" +
            "}\n" +
            "\n" +
            "fn viaMany(q: [*]const u8) u8 {\n" +
            "    return classify(q) + isOne(q);\n" +
            "}\n" +
            "\n" +
            "fn viaC(q: [*c]const u8) u8 {\n" +
            "    return classify(q);\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var s = S{ .a = 5 };\n" +
            "    const buf = [_]u8{ 7, 8, 9 };\n" +
            "    const sl: []const u8 = &buf;\n" +
            "    const one: *S = &s;\n" +
            "    var total: u8 = 0;\n" +
            "    total += classify(&s);\n" +
            "    total += classify(one) * 3;\n" +
            "    total += classify(sl.ptr) * 5;\n" +
            "    total += viaMany(&buf);\n" +
            "    total += viaC(&buf) * 7;\n" +
            "    total += classify(sl) * 11;\n" +
            "    total += isOne(&s);\n" +
            "    total += s.a;\n" +
            "    return total;\n" +
            "}\n", 116, "" },
        // Task #124: an in-function struct's method reads the enclosing generic instance's local type alias (`const P =
        // @TypeOf(p);`) from ITS instance: two same-layout structs, each cast and dispatched to its own `get`.
        new object[] { "local_struct_closes_over_alias",
            "const A = struct {\n" +
            "    v: u8,\n" +
            "    fn get(self: *A) u8 {\n" +
            "        return self.v + 1;\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "const B = struct {\n" +
            "    v: u8,\n" +
            "    fn get(self: *B) u8 {\n" +
            "        return self.v * 3;\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "fn call(p: anytype) u8 {\n" +
            "    const P = @TypeOf(p);\n" +
            "    const gen = struct {\n" +
            "        fn run(q: *anyopaque) u8 {\n" +
            "            const self: P = @ptrCast(@alignCast(q));\n" +
            "            return self.get();\n" +
            "        }\n" +
            "    };\n" +
            "    return gen.run(p);\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var a = A{ .v = 10 };\n" +
            "    var b = B{ .v = 10 };\n" +
            "    return call(&a) + call(&b) * 2;\n" +
            "}\n", 71, "" },
        // Task #123: enum members valued by the root unit's own functions (a plain one also called at runtime, a generic
        // declared after the enum), and a container const valued by one.
        new object[] { "enum_member_valued_by_own_fn",
            "const Color = enum(u16) {\n" +
            "    red = base() + 1,\n" +
            "    green = maxOf(u8) - 5,\n" +
            "    blue,\n" +
            "    fn weight(self: Color) u16 {\n" +
            "        return @intFromEnum(self) % 17;\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "fn base() u16 {\n" +
            "    return 40;\n" +
            "}\n" +
            "\n" +
            "fn maxOf(comptime T: type) T {\n" +
            "    return ~@as(T, 0);\n" +
            "}\n" +
            "\n" +
            "const Box = struct {\n" +
            "    size: u16,\n" +
            "    const default_size = base() * 2;\n" +
            "};\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const b = Box{ .size = Box.default_size };\n" +
            "    var total: u16 = base();\n" +
            "    total += @intFromEnum(Color.red) + @intFromEnum(Color.blue);\n" +
            "    total += Color.green.weight();\n" +
            "    total += b.size;\n" +
            "    return @truncate(total);\n" +
            "}\n", 168, "" },
        // Task #108: a comptime `@Vector(3, u8)` (a shape .NET vectors cannot hold) is an array at compile time; a reified
        // method taking one is analysed only if called; `@FieldType(P, "b")` is the field's type.
        new object[] { "comptime_vector_and_field_type",
            "inline fn iota(comptime n: usize) @Vector(n, u8) {\n" +
            "    comptime {\n" +
            "        var out: [n]u8 = undefined;\n" +
            "        for (&out, 0..) |*e, i| e.* = @intCast(i * 3);\n" +
            "        return out;\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "fn Box(comptime T: type) type {\n" +
            "    return struct {\n" +
            "        v: T,\n" +
            "        const Self = @This();\n" +
            "        fn widen(self: *Self, lanes: @Vector(3, u8)) void {\n" +
            "            _ = self;\n" +
            "            _ = lanes;\n" +
            "        }\n" +
            "        fn get(self: Self) T {\n" +
            "            return self.v;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "const P = struct { a: u8, b: u32 };\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const v = comptime iota(3);\n" +
            "    const arr: [3]u8 = v;\n" +
            "    const b = Box(u8){ .v = 40 };\n" +
            "    var wide: @FieldType(P, \"b\") = 70000;\n" +
            "    wide += 1;\n" +
            "    return arr[2] + arr[1] + b.get() + @as(u8, @intCast(wide % 7));\n" +
            "}\n", 50, "" },
        // Task #132: the declared width of a range-for capture (usize) and of an arithmetic result (equal-width operands,
        // or one an integer literal), read by `@typeInfo(@TypeOf(e)).int.bits`.
        new object[] { "declared_width_of_captures_and_arithmetic",
            "pub fn main() u8 {\n" +
            "    var total: u8 = 0;\n" +
            "    for (0..2) |i| total += @intCast(@typeInfo(@TypeOf(i * i)).int.bits);\n" +
            "    const x: u32 = 7;\n" +
            "    total += @typeInfo(@TypeOf(x + 1)).int.bits;\n" +
            "    const y: u8 = 3;\n" +
            "    total += @typeInfo(@TypeOf(y << 2)).int.bits;\n" +
            "    total += @typeInfo(@TypeOf(y *% y)).int.bits;\n" +
            "    return total;\n" +
            "}\n", 176, "" },
        // Task #134: zig's `\xNN` is exactly two hex digits (`"ab\x00cd"` is 5 bytes, not C's 3), and a `\u{e9}` byte
        // followed by a hex-looking character stays two bytes.
        new object[] { "zig_hex_escapes_two_digits",
            "pub fn main() u8 {\n" +
            "    const a = \"ab\\x00cd\";\n" +
            "    const b = \"\\x41BC\\u{e9}a\";\n" +
            "    const c = \"x\\x41\";\n" +
            "    var n: u32 = a.len * 10 + a[4] % 10;\n" +
            "    n += b.len * 3 + b[1] + b[3] + b[5] + c.len;\n" +
            "    return @truncate(n);\n" +
            "}\n", 172, "" },
        // Task #131: a value-position `inline for … else` over a `[_]type{…}` list, single and indexed, yielding through
        // `break v` or the `else` value.
        new object[] { "inline_for_value_loop",
            "fn firstAtLeast(comptime bits: u16) u8 {\n" +
            "    return inline for ([_]type{ u8, u16, u32, u64 }, 0..) |T, i| {\n" +
            "        if (@bitSizeOf(T) >= bits) break @intCast(i);\n" +
            "    } else 255;\n" +
            "}\n" +
            "\n" +
            "fn sizeOfTwo() u8 {\n" +
            "    return inline for ([_]type{ u8, u16, u32 }) |T| {\n" +
            "        if (@sizeOf(T) == 2) break @as(u8, @bitSizeOf(T));\n" +
            "    } else 0;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    return firstAtLeast(9) * 100 + firstAtLeast(64) * 10 + firstAtLeast(128) % 7 + sizeOfTwo();\n" +
            "}\n", 149, "" },
        // Task #129: `@alignCast(@fieldParentPtr("b", b))` at a `*P` result: the parent pointer, written through.
        new object[] { "align_cast_field_parent_ptr",
            "const P = struct { a: u32, b: u8 };\n" +
            "\n" +
            "fn parentOf(b: *u8) *P {\n" +
            "    const p: *P = @alignCast(@fieldParentPtr(\"b\", b));\n" +
            "    return p;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var x = P{ .a = 5, .b = 7 };\n" +
            "    const q = parentOf(&x.b);\n" +
            "    q.a += 1;\n" +
            "    return @intCast(x.a * 10 + q.b);\n" +
            "}\n", 67, "" },
        // Task #130: a labeled block statement (`if (c) lbl: { … break :lbl; }`, a bare `blk: { … }` in a loop).
        new object[] { "labeled_block_statement",
            "fn grow(len: u32, want: u32) u32 {\n" +
            "    var out: u32 = len;\n" +
            "    if (len != want) realloc: {\n" +
            "        if (want < len) {\n" +
            "            out = want;\n" +
            "            break :realloc;\n" +
            "        }\n" +
            "        out = want * 2;\n" +
            "    }\n" +
            "    return out;\n" +
            "}\n" +
            "\n" +
            "fn scan(xs: []const u8) u32 {\n" +
            "    var hits: u32 = 0;\n" +
            "    for (xs) |x| {\n" +
            "        blk: {\n" +
            "            if (x == 0) break :blk;\n" +
            "            if (x > 9) continue;\n" +
            "            hits += x;\n" +
            "        }\n" +
            "        hits += 1;\n" +
            "    }\n" +
            "    return hits;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const a = grow(4, 4);\n" +
            "    const b = grow(8, 3);\n" +
            "    const c = grow(2, 5);\n" +
            "    const d = scan(&[_]u8{ 1, 0, 20, 3 });\n" +
            "    return @intCast(a + b + c + d);\n" +
            "}\n", 24, "" },
        // Task #130: the statement `while (c) body else elsebody`, with `break`, `continue`, a returning else and nesting.
        new object[] { "while_else_statement",
            "fn firstSet(masks: []const u8) ?usize {\n" +
            "    var offset: usize = 0;\n" +
            "    while (offset < masks.len) {\n" +
            "        if (masks[offset] != 0) break;\n" +
            "        offset += 1;\n" +
            "    } else return null;\n" +
            "    return offset;\n" +
            "}\n" +
            "\n" +
            "fn sumPairs(n: u32) u32 {\n" +
            "    var total: u32 = 0;\n" +
            "    var i: u32 = 0;\n" +
            "    while (i < n) {\n" +
            "        var j: u32 = 0;\n" +
            "        while (j < i) {\n" +
            "            j += 1;\n" +
            "            if (j == 3) continue;\n" +
            "            if (j == 5) break;\n" +
            "            total += j;\n" +
            "        } else total += 10;\n" +
            "        i += 1;\n" +
            "    } else total += 100;\n" +
            "    return total;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const a = firstSet(&[_]u8{ 0, 0, 7, 1 }) orelse 99;\n" +
            "    const b = firstSet(&[_]u8{ 0, 0 }) orelse 9;\n" +
            "    return @intCast(a + b + sumPairs(4));\n" +
            "}\n", 158, "" },
        // Task #130: a decl literal behind `try` (`.inner = try .init(n)`, `var q: Inner = try .init(1);`).
        new object[] { "decl_literal_through_try",
            "const Inner = struct {\n" +
            "    n: u32,\n" +
            "    pub fn init(n: u32) error{Bad}!Inner {\n" +
            "        if (n > 100) return error.Bad;\n" +
            "        return .{ .n = n * 2 };\n" +
            "    }\n" +
            "    pub fn plain(n: u32) Inner {\n" +
            "        return .{ .n = n + 1 };\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "const Outer = struct {\n" +
            "    inner: Inner,\n" +
            "    tag: u8,\n" +
            "    fn make(n: u32) !Outer {\n" +
            "        return Outer{ .inner = try .init(n), .tag = 3 };\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "pub fn main() !u8 {\n" +
            "    const o = try Outer.make(10);\n" +
            "    const p: Outer = .{ .inner = .plain(4), .tag = 1 };\n" +
            "    var q: Inner = try .init(1);\n" +
            "    q = try .init(2);\n" +
            "    const bad: u8 = if (Outer.make(500)) |_| 0 else |_| 7;\n" +
            "    return @intCast(o.inner.n + o.tag + p.inner.n + p.tag + q.n + bad);\n" +
            "}\n", 40, "" },
        // Task #130: an array container `var` and a comptime-bounded slice of it at a `[*]T` field default.
        new object[] { "container_array_var",
            "const Set = struct {\n" +
            "    len: usize = 0,\n" +
            "    masks: [*]u32 = empty_masks_ptr,\n" +
            "\n" +
            "    var empty_masks_data = [_]u32{ 0, undefined };\n" +
            "    const empty_masks_ptr = empty_masks_data[1..2];\n" +
            "\n" +
            "    fn header(self: Set) u32 {\n" +
            "        return (self.masks - 1)[0];\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "var counter: u32 = 5;\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const s: Set = .{};\n" +
            "    counter += 1;\n" +
            "    return @intCast(s.header() + s.len + counter);\n" +
            "}\n", 6, "" },
        // Task #130: `x catch unreachable;` and `x catch {};` over a `!void`, at top level and in a method.
        new object[] { "catch_unreachable_void",
            "var hits: u8 = 0;\n" +
            "\n" +
            "fn bump(n: u8) error{Big}!void {\n" +
            "    if (n > 9) return error.Big;\n" +
            "    hits += n;\n" +
            "}\n" +
            "\n" +
            "const S = struct {\n" +
            "    n: u8,\n" +
            "    fn reset(self: *S) void {\n" +
            "        bump(self.n) catch unreachable;\n" +
            "        self.n = 0;\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var s: S = .{ .n = 4 };\n" +
            "    s.reset();\n" +
            "    bump(3) catch unreachable;\n" +
            "    bump(20) catch {};\n" +
            "    return hits + s.n;\n" +
            "}\n", 7, "" },
        // Task #108: an untyped named literal's anonymous struct type, read at runtime and walked by a `for`.
        new object[] { "anon_struct_literal",
            "const S = struct {\n" +
            "    const sizes = blk: {\n" +
            "        var a: [2]usize = .{ 1, 2 };\n" +
            "        a[0] = 5;\n" +
            "        break :blk .{ .bytes = a, .n = 3 };\n" +
            "    };\n" +
            "};\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var total: usize = S.sizes.n;\n" +
            "    for (S.sizes.bytes) |b| total += b;\n" +
            "    return @intCast(total + S.sizes.bytes[0]);\n" +
            "}\n", 15, "" },
        // Task #108: a `comptime_int` labeled-block const meeting a `usize` peer under `+|`.
        new object[] { "comptime_int_block_const",
            "const S = struct {\n" +
            "    const init_capacity: comptime_int = init: {\n" +
            "        var max: comptime_int = 1;\n" +
            "        for ([_]u8{ 2, 8, 4 }) |x| max = @max(max, x);\n" +
            "        break :init @max(1, 64 / max);\n" +
            "    };\n" +
            "    fn grow(minimum: usize) usize {\n" +
            "        return minimum +| (minimum / 2 + init_capacity);\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    return @intCast(S.grow(10));\n" +
            "}\n", 23, "" },
        // Task #108: `inline for` over arrays (value and `*`) with a type list, an enum const as a comptime
        // argument, and `@bitSizeOf` of a uniform struct.
        new object[] { "inline_for_arrays_and_lists",
            "const E = enum(u8) { a, b, c };\n" +
            "\n" +
            "fn weight(comptime e: E) u32 {\n" +
            "    return switch (e) {\n" +
            "        .a => 1,\n" +
            "        .b => 10,\n" +
            "        .c => 100,\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "const D = struct { x: u64, y: u64 };\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const in = [_]u32{ 1, 2, 3 };\n" +
            "    var out: [3]u32 = undefined;\n" +
            "    inline for (in, &out, [_]type{ u8, u16, u32 }) |x, *o, t| {\n" +
            "        o.* = x * @sizeOf(t);\n" +
            "    }\n" +
            "    var total: u32 = out[0] + out[1] + out[2];\n" +
            "    inline for (0..3) |i| {\n" +
            "        const e = @as(E, @enumFromInt(i));\n" +
            "        total += weight(e);\n" +
            "    }\n" +
            "    return @intCast(total + @bitSizeOf(D) / 8);\n" +
            "}\n", 144, "" },
        // Task #135: a static call named `alloc`, an exhaustive returning enum switch, `@sizeOf`/`@bitSizeOf` of `void`,
        // `void` fields and comparisons, and a comptime bool seed choosing a value arm.
        new object[] { "void_fields_and_static_alloc",
            "const Header = struct {\n" +
            "    n: u8,\n" +
            "    fn alloc(n: u8) Header {\n" +
            "        return .{ .n = n };\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "const Kind = enum { a, b, c };\n" +
            "\n" +
            "fn size(k: Kind) usize {\n" +
            "    switch (k) {\n" +
            "        .a => return 1,\n" +
            "        .b => return 2,\n" +
            "        .c => return 4,\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "fn Box(comptime T: type, comptime keep: bool) type {\n" +
            "    return struct {\n" +
            "        tag: Tag,\n" +
            "        val: T,\n" +
            "        const Tag = if (keep) u32 else void;\n" +
            "        fn same(self: @This(), t: Tag) bool {\n" +
            "            return self.tag == t;\n" +
            "        }\n" +
            "        fn pick(self: @This(), other: T) T {\n" +
            "            return if (keep) self.val else other;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const h = Header.alloc(5);\n" +
            "    var b: Box(u8, false) = .{ .tag = {}, .val = 7 };\n" +
            "    b.tag = {};\n" +
            "    const c: Box(u8, true) = .{ .tag = 9, .val = 1 };\n" +
            "    var total: usize = h.n + size(.c) + @sizeOf(void) + @bitSizeOf(void);\n" +
            "    if (b.same({})) total += 10;\n" +
            "    if (c.same(9)) total += 100;\n" +
            "    total += b.pick(20) + c.pick(20);\n" +
            "    return @intCast(total + b.val + c.val);\n" +
            "}\n", 148, "" },
        // Task #136: unsigned `%` and `/` over an index, field and deref read (the declared type, not C#'s int promotion).
        new object[] { "unsigned_div_mod_reads",
            "const Box = struct { n: u16 };\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var out = [_]u16{ 0, 0x1e9, 3 };\n" +
            "    _ = &out;\n" +
            "    const e: []const u8 = \"a7\";\n" +
            "    var b = Box{ .n = 47 };\n" +
            "    _ = &b;\n" +
            "    const p = &b;\n" +
            "    return @intCast((out[1] & 0xff) % 10 + (e[1] - '0') % 10 + p.n % 10 + b.n / 7 + p.*.n % 3);\n" +
            "}\n", 25, "" },
        // Task #139: `u64` comptime arguments past i64 (std.hash.Fnv1a_64's offset basis) key, seed, compare and shift unsigned.
        new object[] { "wide_u64_comptime_args",
            "fn Hash(comptime T: type, comptime prime: T, comptime offset: T) type {\n" +
            "    return struct {\n" +
            "        value: T = offset,\n" +
            "        const top: T = offset >> 60;\n" +
            "        fn high() bool {\n" +
            "            return offset > 0x8000000000000000;\n" +
            "        }\n" +
            "        fn mix(self: *@This(), b: u8) void {\n" +
            "            self.value ^= b;\n" +
            "            self.value *%= prime;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "const A = Hash(u64, 0x100000001b3, 0xcbf29ce484222325);\n" +
            "const B = Hash(u64, 3, 0xffffffffffffffff);\n" +
            "pub fn main() u8 {\n" +
            "    var a: A = .{};\n" +
            "    a.mix('a');\n" +
            "    var b: B = .{};\n" +
            "    b.mix(1);\n" +
            "    const t: u8 = @intCast(A.top);\n" +
            "    return @as(u8, @truncate(a.value)) +% t +% @as(u8, @intFromBool(A.high())) +% @as(u8, @truncate(b.value >> 56));\n" +
            "}\n", 152, "" },
        // Task #141: else-less `if` prongs over an expression, with a comptime subject (an untaken @compileError) and a runtime one.
        new object[] { "else_less_if_prongs",
            "const Kind = enum { one, many, slice };\n" +
            "fn check(comptime k: Kind, comptime n: u8) u8 {\n" +
            "    switch (k) {\n" +
            "        .slice => {},\n" +
            "        .one => if (n > 3) @compileError(\"n too big\"),\n" +
            "        .many => if (n == 0) @compileError(\"n is zero\"),\n" +
            "    }\n" +
            "    return n;\n" +
            "}\n" +
            "fn inc(p: *u8) void {\n" +
            "    p.* += 2;\n" +
            "}\n" +
            "fn count(v: u8, hits: *u8) void {\n" +
            "    switch (v) {\n" +
            "        0 => {},\n" +
            "        1 => if (hits.* < 10) inc(hits),\n" +
            "        else => if (v > 5) inc(hits),\n" +
            "    }\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var hits: u8 = 0;\n" +
            "    for ([_]u8{ 0, 1, 7, 3, 9, 1 }) |v| count(v, &hits);\n" +
            "    return check(.one, 2) + check(.many, 5) + hits;\n" +
            "}\n", 15, "" },
        // Task #146: `catch |e| return if (c) a else b`, taking both returns and the success path.
        new object[] { "catch_return_if",
            "const E = error{ Big, Odd };\n" +
            "fn f(x: u8) E!u8 {\n" +
            "    if (x > 30) return error.Big;\n" +
            "    if (x % 2 == 1) return error.Odd;\n" +
            "    return x;\n" +
            "}\n" +
            "fn g(x: u8) u8 {\n" +
            "    const v = f(x) catch |e| return if (e == error.Big) 7 else 8;\n" +
            "    return v + 1;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return g(40) + g(3) * 2 + g(10) * 3;\n" +
            "}\n", 56, "" },
        // Task #140: std.crypto.blake3's shapes: a late-declared struct const in a field extent, `@intCast` slice bounds,
        // open slices of a many-item pointer, and an array local copied through a pointer.
        new object[] { "blake3_shapes",
            "const Chunk = struct {\n" +
            "    buf: [Hasher.block_length]u8,\n" +
            "    len: u8,\n" +
            "};\n" +
            "const Hasher = struct {\n" +
            "    pub const block_length = 4;\n" +
            "    chunk: Chunk,\n" +
            "};\n" +
            "fn sum2(p: [*]const u8) u32 {\n" +
            "    const q = p[2..];\n" +
            "    const w = q[0..2];\n" +
            "    return @as(u32, w[0]) + w[1];\n" +
            "}\n" +
            "fn rotate(v: *[3]u8) void {\n" +
            "    const t: [3]u8 = v.*;\n" +
            "    v[0] = t[2];\n" +
            "    v[1] = t[0];\n" +
            "    v[2] = t[1];\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const h = Hasher{ .chunk = .{ .buf = .{ 1, 2, 3, 4 }, .len = 4 } };\n" +
            "    const bytes = [_]u8{ 1, 2, 3, 4, 5 };\n" +
            "    const p: [*]const u8 = &bytes;\n" +
            "    var n: u64 = 1;\n" +
            "    _ = &n;\n" +
            "    const head = bytes[0..@intCast(n)];\n" +
            "    const tail = bytes[@intCast(n)..];\n" +
            "    var a = [_]u8{ 1, 2, 3 };\n" +
            "    rotate(&a);\n" +
            "    const total = h.chunk.buf.len + h.chunk.buf[3] + sum2(p) + sum2(p + 1) + head.len + tail.len * 2 + a[0] * 3;\n" +
            "    return @intCast(total);\n" +
            "}\n", 42, "" },
        // Task #143: a capture switch in a sub-expression over `@abs` of a comptime_int (std.math.IntFittingRange's shape).
        new object[] { "comptime_abs_capture_switch",
            "fn log2(comptime x: comptime_int) comptime_int {\n" +
            "    var n: comptime_int = 0;\n" +
            "    var v = x;\n" +
            "    while (v > 1) : (v >>= 1) n += 1;\n" +
            "    return n;\n" +
            "}\n" +
            "fn bitsFor(comptime from: comptime_int, comptime to: comptime_int) u16 {\n" +
            "    return @as(u16, @intFromBool(from < 0)) + switch (if (from < 0) @max(@abs(from) - 1, to) else to) {\n" +
            "        0 => 0,\n" +
            "        else => |pos_max| 1 + log2(pos_max),\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return @intCast(bitsFor(-1, 1) * 10 + bitsFor(0, 9) + bitsFor(0, 0) * 100);\n" +
            "}\n", 24, "" },
        // Task #144: a vector stored through an array view, an anonymous list into one, and u8 lanes widening to u16.
        new object[] { "vector_store_and_widen",
            "fn total(v: @Vector(8, u16)) u16 {\n" +
            "    return @reduce(.Add, v);\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var buf = [_]u16{ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };\n" +
            "    const small: @Vector(8, u8) = .{ 1, 2, 3, 4, 5, 6, 7, 250 };\n" +
            "    const v: @Vector(8, u16) = .{ 10, 20, 30, 40, 1, 1, 1, 1 };\n" +
            "    buf[2..][0..8].* = v;\n" +
            "    buf[10..][0..2].* = .{ 7, 9 };\n" +
            "    const w = total(small);\n" +
            "    var sum: u16 = 0;\n" +
            "    for (buf) |x| sum += x;\n" +
            "    return @intCast((sum + w) % 256);\n" +
            "}\n", 142, "" },
        // Task #142: std.meta.Tag / activeTag's shape: a union's tag_type through `orelse` in a type position, and `@as(Tag(U), u)`.
        new object[] { "union_tag_type",
            "const U = union(enum) { a: u8, b: u16, c };\n" +
            "fn Tag(comptime T: type) type {\n" +
            "    return switch (@typeInfo(T)) {\n" +
            "        .@\"enum\" => |info| info.tag_type,\n" +
            "        .@\"union\" => |info| info.tag_type orelse @compileError(\"no tag\"),\n" +
            "        else => @compileError(\"bad\"),\n" +
            "    };\n" +
            "}\n" +
            "fn activeTag(u: anytype) Tag(@TypeOf(u)) {\n" +
            "    return @as(Tag(@TypeOf(u)), u);\n" +
            "}\n" +
            "const Kind = enum(u8) { small = 3, big = 7 };\n" +
            "const W = union(Kind) { small: u8, big: u32 };\n" +
            "pub fn main() u8 {\n" +
            "    const x: U = .{ .b = 5 };\n" +
            "    const y: U = .c;\n" +
            "    const t = activeTag(x);\n" +
            "    const k = activeTag(W{ .big = 9 });\n" +
            "    return @intFromEnum(k) * 20 + @as(u8, @intFromEnum(t)) * 10 + @intFromEnum(activeTag(y)) + @as(u8, @intFromBool(t == .b));\n" +
            "}\n", 153, "" },
        // Task #147: `.ptr` and `.len` through a pointer to an array (an anytype `&arr`).
        new object[] { "ptr_len_through_array_pointer",
            "fn first(p: anytype) u8 {\n" +
            "    const q = p.ptr;\n" +
            "    return q[0] + q[2] + @as(u8, @intCast(p.len));\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var a = [_]u8{ 5, 6, 7 };\n" +
            "    const b = [_]u8{ 10, 20, 30, 40 };\n" +
            "    return first(&a) + first(&b);\n" +
            "}\n", 59, "" },
        // Task #148: std.math.rotr's vector shape: a lane type read through `@typeInfo(T).vector.child`, a vector shifted by a
        // `@splat` count, and `@as(V, @splat(1))`.
        new object[] { "vector_rotate_by_splat",
            "fn Rot(comptime T: type) type {\n" +
            "    return struct {\n" +
            "        fn rotr(x: T, ar: u5) T {\n" +
            "            const C = @typeInfo(T).vector.child;\n" +
            "            if (@typeInfo(C).int.bits != 32) @compileError(\"expected u32 lanes\");\n" +
            "            return (x >> @splat(ar)) | (x << @splat(1 +% ~ar));\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const V = @Vector(8, u32);\n" +
            "    const v: V = .{ 1, 0x80000000, 3, 0xF0, 5, 6, 7, 8 };\n" +
            "    const r = Rot(V).rotr(v, 4);\n" +
            "    const w = v + @as(V, @splat(1));\n" +
            "    return @truncate(r[0] >> 24 ^ r[3] ^ w[1] >> 24 ^ r[7]);\n" +
            "}\n", 159, "" },
        // Task #149: a user function returning `?comptime_int` folds at compile time; its null takes `orelse break :blk` then.
        new object[] { "comptime_optional_int_orelse_break",
            "fn pick(comptime T: type) ?comptime_int {\n" +
            "    return if (@sizeOf(T) == 2) 8 else null;\n" +
            "}\n" +
            "fn lanes(comptime T: type) usize {\n" +
            "    blk: {\n" +
            "        const n = pick(T) orelse break :blk;\n" +
            "        const V = @Vector(n, u8);\n" +
            "        const v: V = @splat(3);\n" +
            "        return n + @reduce(.Add, v);\n" +
            "    }\n" +
            "    return 1;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return @intCast(lanes(u16) + lanes(u32) * 100);\n" +
            "}\n", 132, "" },
        // Task #149: `@select`, relational masks and `@reduce` over 64-bit and 512-bit vectors.
        new object[] { "vector_widths_select_reduce",
            "pub fn main() u8 {\n" +
            "    const a: @Vector(8, u8) = .{ 9, 2, 7, 4, 5, 6, 1, 8 };\n" +
            "    const b: @Vector(8, u8) = @splat(5);\n" +
            "    const lt = a < b;\n" +
            "    const pick = @select(u8, lt, a, b);\n" +
            "    const mn = @reduce(.Min, a);\n" +
            "    const mx = @reduce(.Max, pick);\n" +
            "    const c: @Vector(16, u32) = .{ 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };\n" +
            "    const d: @Vector(16, u32) = @splat(8);\n" +
            "    const ge = c >= d;\n" +
            "    const sel = @select(u32, ge, c, d);\n" +
            "    const s = @reduce(.Add, sel);\n" +
            "    const top = @reduce(.Max, c) - @reduce(.Min, c);\n" +
            "    return mn + mx * 3 + @as(u8, @intCast(s % 50)) + @as(u8, @intCast(top)) + a[3] + @as(u8, @intCast(c[15]));\n" +
            "}\n", 65, "" },
        // Task #150: a pointer type argument's size class inside a generic function and a reified struct's const.
        new object[] { "pointer_size_class_in_generics",
            "fn Kind(comptime T: type) type {\n" +
            "    return struct {\n" +
            "        const size: u8 = switch (@typeInfo(T).pointer.size) {\n" +
            "            .one => 1,\n" +
            "            .many => 2,\n" +
            "            .slice => 3,\n" +
            "            .c => 4,\n" +
            "        };\n" +
            "    };\n" +
            "}\n" +
            "fn kind(comptime T: type) u8 {\n" +
            "    return switch (@typeInfo(T).pointer.size) {\n" +
            "        .one => 10,\n" +
            "        .many => 20,\n" +
            "        .slice => 30,\n" +
            "        .c => 40,\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return Kind(*const u8).size + Kind([*]const u8).size * 3 + Kind([]const u8).size * 7 + kind(*u16) + kind([*]u16) * 2;\n" +
            "}\n", 78, "" },
        // Task #151: `?[N]T` as a struct field with a null default, a local assigned null and a list, `== null` and a capture.
        new object[] { "optional_array_field_and_local",
            "const Options = struct { key: ?[4]u8 = null, n: u8 = 1 };\n" +
            "fn sum(o: Options) u8 {\n" +
            "    var s: u8 = o.n;\n" +
            "    if (o.key) |k| {\n" +
            "        for (k) |b| s +%= b;\n" +
            "    }\n" +
            "    return s;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var local: ?[3]u16 = null;\n" +
            "    var t: u16 = 0;\n" +
            "    if (local == null) t += 100;\n" +
            "    local = .{ 1, 2, 3 };\n" +
            "    if (local) |arr| t += arr[2];\n" +
            "    const a = sum(.{});\n" +
            "    const b = sum(.{ .key = .{ 1, 2, 3, 4 }, .n = 5 });\n" +
            "    return a + b + @as(u8, @intCast(t));\n" +
            "}\n", 119, "" },
        // Task #151: `?[N]T` returned, passed, copied, unwrapped with `.?`, compared with null and given an `orelse` fallback.
        new object[] { "optional_array_return_and_orelse",
            "fn maybe(n: u8) ?[2]u32 {\n" +
            "    if (n == 0) return null;\n" +
            "    return .{ n, n * 2 };\n" +
            "}\n" +
            "fn first(o: ?[2]u32) u32 {\n" +
            "    return if (o) |a| a[0] + a[1] else 7;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const x = maybe(3);\n" +
            "    const y = maybe(0);\n" +
            "    var copy = x;\n" +
            "    copy = .{ 100, 100 };\n" +
            "    const z = x.?;\n" +
            "    const flag: u32 = if (y != null) 1000 else 0;\n" +
            "    const fallback = [2]u32{ 4, 5 };\n" +
            "    const got = y orelse fallback;\n" +
            "    return @intCast(first(x) + first(y) + z[1] + flag + (copy.?)[0] / 10 + got[0] * got[1]);\n" +
            "}\n", 52, "" },
        // Task #151: `var got = y orelse fallback;` is a copy, so writing `got` leaves `fallback` alone (it aliased before).
        new object[] { "optional_array_orelse_copies",
            "pub fn main() u8 {\n" +
            "    const y: ?[2]u8 = null;\n" +
            "    const fallback = [2]u8{ 4, 5 };\n" +
            "    var got = y orelse fallback;\n" +
            "    got[0] = 9;\n" +
            "    const w = [2]u8{ 1, 2 };\n" +
            "    const c = true;\n" +
            "    var sel = if (c) w else fallback;\n" +
            "    sel[1] = 7;\n" +
            "    return fallback[0] * 10 + w[1] + got[0] + sel[1];\n" +
            "}\n", 58, "" },
        // Task #152: a multi-dimensional array literal, element update and indexing.
        new object[] { "multi_dimensional_array_literal",
            "pub fn main() u8 {\n" +
            "    var g: [2][3]u8 = .{ .{ 1, 2, 3 }, .{ 4, 5, 6 } };\n" +
            "    g[1][0] += 10;\n" +
            "    const h = [2][2]u8{ .{ 7, 8 }, .{ 9, 1 } };\n" +
            "    return g[1][0] + g[0][2] + h[1][0];\n" +
            "}\n", 26, "" },
        // Task #152: slices of arrays: `&grid` and `grid[1..3]` as `[][3]u8`, value and pointer row captures, `.len`.
        new object[] { "slice_of_arrays",
            "fn sumRows(rows: []const [3]u8) u32 {\n" +
            "    var s: u32 = 0;\n" +
            "    for (rows) |r| s += r[0] + r[1] * 2 + r[2] * 3;\n" +
            "    return s;\n" +
            "}\n" +
            "fn bump(rows: [][3]u8) void {\n" +
            "    for (rows) |*r| r[1] += 1;\n" +
            "    rows[0][2] = 9;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var grid: [4][3]u8 = .{ .{ 1, 2, 3 }, .{ 4, 5, 6 }, .{ 7, 8, 9 }, .{ 0, 0, 1 } };\n" +
            "    const mid = grid[1..3];\n" +
            "    bump(mid);\n" +
            "    const row: [3]u8 = mid[1];\n" +
            "    const n = mid.len;\n" +
            "    return @intCast(sumRows(&grid) % 200 + row[1] + n + grid[1][2]);\n" +
            "}\n", 132, "" },
        // Task #152: a three-dimensional array with a spliced named row and a copied plane.
        new object[] { "three_dimensional_array",
            "const S = struct { r: [2]u8 };\n" +
            "pub fn main() u8 {\n" +
            "    const s = S{ .r = .{ 7, 8 } };\n" +
            "    var cube: [2][2][2]u8 = .{ .{ .{ 1, 2 }, .{ 3, 4 } }, .{ s.r, .{ 5, 6 } } };\n" +
            "    cube[1][0][1] += 1;\n" +
            "    const plane = cube[1];\n" +
            "    return cube[1][0][1] * 10 + cube[0][1][0] + plane[1][1] + s.r[1];\n" +
            "}\n", 107, "" },
        // Task #153: slices of pointers (`[]const [*]const u8`, `[]*u8`): iteration, element derefs, slicing, `&arr`.
        new object[] { "slice_of_pointers",
            "fn total(inputs: []const [*]const u8, n: usize) u32 {\n" +
            "    var s: u32 = 0;\n" +
            "    for (inputs) |p| {\n" +
            "        var i: usize = 0;\n" +
            "        while (i < n) : (i += 1) s += p[i];\n" +
            "    }\n" +
            "    return s;\n" +
            "}\n" +
            "fn swapFirst(ptrs: []*u8) void {\n" +
            "    const t = ptrs[0].*;\n" +
            "    ptrs[0].* = ptrs[1].*;\n" +
            "    ptrs[1].* = t;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const a = [_]u8{ 1, 2, 3 };\n" +
            "    const b = [_]u8{ 10, 20, 30 };\n" +
            "    var ptrs = [_][*]const u8{ &a, &b };\n" +
            "    const sl: []const [*]const u8 = &ptrs;\n" +
            "    ptrs[1] = &a;\n" +
            "    var x: u8 = 5;\n" +
            "    var y: u8 = 7;\n" +
            "    var refs = [_]*u8{ &x, &y };\n" +
            "    swapFirst(&refs);\n" +
            "    return @intCast(total(sl, 3) + total(sl[0..1], 2) + x * 10 + y + sl.len);\n" +
            "}\n", 92, "" },
        // Task #154: an `undefined` 2-D local filled in a loop, whole-row assignment into a struct field and back.
        new object[] { "multi_dimensional_undefined_and_row_assign",
            "const Stack = struct {\n" +
            "    rows: [4][3]u8 = undefined,\n" +
            "    len: usize = 0,\n" +
            "    fn push(self: *Stack, r: [3]u8) void {\n" +
            "        self.rows[self.len] = r;\n" +
            "        self.len += 1;\n" +
            "    }\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    var t: [2][3]u8 = undefined;\n" +
            "    for (0..2) |i| {\n" +
            "        for (0..3) |j| t[i][j] = @intCast(i * 10 + j);\n" +
            "    }\n" +
            "    var s = Stack{};\n" +
            "    s.push(.{ 1, 2, 3 });\n" +
            "    s.push(t[1]);\n" +
            "    t[0] = s.rows[0];\n" +
            "    t[0][0] = 99;\n" +
            "    return s.rows[1][2] + t[0][1] + s.rows[0][0] + t[0][0] / 3;\n" +
            "}\n", 48, "" },
        // Task #155: vector lane stores in `inline for`s, into a variable and into an array element, and a compound one.
        new object[] { "vector_lane_store",
            "fn transpose(comptime n: comptime_int, vecs: *[n]@Vector(n, u32)) void {\n" +
            "    const temp: [n]@Vector(n, u32) = vecs.*;\n" +
            "    inline for (0..n) |i| {\n" +
            "        inline for (0..n) |j| {\n" +
            "            vecs[i][j] = temp[j][i];\n" +
            "        }\n" +
            "    }\n" +
            "}\n" +
            "fn lanes(comptime n: usize, base: u32) @Vector(n, u32) {\n" +
            "    var result: @Vector(n, u32) = undefined;\n" +
            "    inline for (0..n) |i| {\n" +
            "        result[i] = base + @as(u32, i) * 3;\n" +
            "    }\n" +
            "    result[1] +%= 100;\n" +
            "    return result;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var m = [4]@Vector(4, u32){ .{ 1, 2, 3, 4 }, .{ 5, 6, 7, 8 }, .{ 9, 10, 11, 12 }, .{ 13, 14, 15, 16 } };\n" +
            "    transpose(4, &m);\n" +
            "    const v = lanes(4, 1);\n" +
            "    return @truncate(m[0][1] + m[1][0] * 10 + m[3][2] + v[0] + v[1] + v[3]);\n" +
            "}\n", 152, "" },
        // Task #156: packed structs of bool fields (one bit each), mixed with narrow integers; @bitCast both ways and @sizeOf.
        new object[] { "packed_struct_bool_bits",
            "const Flags = packed struct(u8) {\n" +
            "    a: bool = false,\n" +
            "    b: bool = false,\n" +
            "    c: bool = false,\n" +
            "    d: bool = false,\n" +
            "    e: bool = false,\n" +
            "    f: bool = false,\n" +
            "    g: bool = false,\n" +
            "    h: bool = false,\n" +
            "    fn toInt(self: Flags) u8 {\n" +
            "        return @bitCast(self);\n" +
            "    }\n" +
            "    fn with(self: Flags, other: Flags) Flags {\n" +
            "        return @bitCast(self.toInt() | other.toInt());\n" +
            "    }\n" +
            "};\n" +
            "const Mixed = packed struct(u16) {\n" +
            "    lo: u3,\n" +
            "    on: bool,\n" +
            "    mid: u4,\n" +
            "    off: bool,\n" +
            "    hi: u7,\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    var f = Flags{ .b = true };\n" +
            "    f = f.with(.{ .d = true, .h = true });\n" +
            "    f.a = true;\n" +
            "    var m = Mixed{ .lo = 5, .on = true, .mid = 9, .off = false, .hi = 3 };\n" +
            "    m.off = true;\n" +
            "    const raw: u16 = @bitCast(m);\n" +
            "    return f.toInt() +% @as(u8, @truncate(raw)) +% @as(u8, @intFromBool(f.d)) +% @as(u8, @truncate(raw >> 8)) +% @sizeOf(Flags) * 10 +% @sizeOf(Mixed) * 100;\n" +
            "}\n", 2, "" },
        // Task #158: mixed-signedness operators zig allows: a wider signed peer, an unsigned peer, and comparisons.
        new object[] { "mixed_signedness_allowed_peers",
            "fn mix(a: i32, b: u8, c: u16, d: i16, e: usize) i64 {\n" +
            "    const widened: i32 = a + b;\n" +
            "    const unsigned_peer: u16 = b + c;\n" +
            "    const lt: i64 = @intFromBool(d < c);\n" +
            "    const eq: i64 = @intFromBool(a == e);\n" +
            "    return widened + unsigned_peer + lt * 100 + eq * 1000 + (d - 3);\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return @intCast(mix(-5, 200, 7, 4, 99) & 0xff);\n" +
            "}\n", 247, "" },
        // Task #159: `create` on a type-returning call's type (a forwarding generic), and a comptime-length slice deref'd
        // into an array copy that is then written.
        new object[] { "type_call_create_method",
            "fn Hasher64(comptime c: usize, comptime d: usize) type {\n" +
            "    return Hasher(u64, c, d);\n" +
            "}\n" +
            "fn Hasher(comptime T: type, comptime c: usize, comptime d: usize) type {\n" +
            "    return struct {\n" +
            "        pub fn create(out: *T, x: T) void {\n" +
            "            out.* = x * c + d;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var out: u64 = 0;\n" +
            "    Hasher64(2, 4).create(&out, 10);\n" +
            "    const H = Hasher64(1, 1);\n" +
            "    var o2: u64 = 0;\n" +
            "    H.create(&o2, 5);\n" +
            "    return @intCast(out + o2);\n" +
            "}\n", 30, "" },
        new object[] { "slice_deref_array_copy",
            "fn sum(b: [4]u8) u32 {\n" +
            "    return b[0] + @as(u32, b[3]) * 10;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var data = [_]u8{ 1, 2, 3, 4, 5, 6, 7, 8 };\n" +
            "    const s: []u8 = &data;\n" +
            "    var off: usize = 0;\n" +
            "    var t: u32 = 0;\n" +
            "    while (off < s.len) : (off += 4) {\n" +
            "        var blob = s[off..][0..4].*;\n" +
            "        blob[0] +%= 100;\n" +
            "        t += sum(blob);\n" +
            "    }\n" +
            "    return @intCast(t % 256 + data[0]);\n" +
            "}\n", 71, "" },
        // Task #160: a reified container const `f(comptime_int, …) catch unreachable` sizing an array parameter.
        new object[] { "comptime_int_catch_container_const",
            "const E = error{DivisionByZero};\n" +
            "fn divCeil(comptime T: type, a: T, b: T) E!T {\n" +
            "    if (b == 0) return error.DivisionByZero;\n" +
            "    return @divFloor(a + b - 1, b);\n" +
            "}\n" +
            "fn H(comptime seed: u8, digest_bits: comptime_int) type {\n" +
            "    return struct {\n" +
            "        pub const digest_length = divCeil(comptime_int, digest_bits, 8) catch unreachable;\n" +
            "        pub fn hash(out: *[digest_length]u8) void {\n" +
            "            for (out, 0..) |*b, i| b.* = @intCast(i + seed);\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var out: [H(3, 40).digest_length]u8 = undefined;\n" +
            "    H(3, 40).hash(&out);\n" +
            "    return out[4] + @as(u8, H(3, 40).digest_length);\n" +
            "}\n", 12, "" },
        // Task #161: a type body's own enum and mode-selected struct with a method (std.crypto.keccak_p's State), a nested
        // container's field default reading the instance seed (sha3's Options), and `volatile` pointees (secureZero).
        new object[] { "type_body_enum_and_selected_struct",
            "const mode = @import(\"builtin\").mode;\n" +
            "fn assert(ok: bool) void {\n" +
            "    if (!ok) unreachable;\n" +
            "}\n" +
            "fn State(comptime f: u11) type {\n" +
            "    comptime assert(f >= 200);\n" +
            "    const Op = enum { uninitialized, initialized, absorb };\n" +
            "    const Tracker = if (mode == .Debug) struct {\n" +
            "        op: Op = .uninitialized,\n" +
            "        fn to(t: *@This(), next: Op) void {\n" +
            "            t.op = next;\n" +
            "        }\n" +
            "    } else struct {\n" +
            "        inline fn to(t: *@This(), next: Op) void {\n" +
            "            _ = t;\n" +
            "            _ = next;\n" +
            "        }\n" +
            "    };\n" +
            "    return struct {\n" +
            "        const Self = @This();\n" +
            "        n: u32,\n" +
            "        transition: Tracker = .{},\n" +
            "        pub fn init(n: u32) Self {\n" +
            "            var s = Self{ .n = n };\n" +
            "            s.transition.to(.initialized);\n" +
            "            return s;\n" +
            "        }\n" +
            "        pub fn absorb(self: *Self, x: u32) void {\n" +
            "            self.transition.to(.absorb);\n" +
            "            self.n +%= x * f;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var s = State(400).init(3);\n" +
            "    s.absorb(2);\n" +
            "    return @truncate(s.n);\n" +
            "}\n", 35, "" },
        new object[] { "nested_container_default_reads_seed",
            "fn K(comptime d: u8) type {\n" +
            "    return struct {\n" +
            "        pub const Options = struct { delim: u8 = d };\n" +
            "        pub fn get(o: Options) u8 {\n" +
            "            return o.delim;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return K(7).get(.{}) * 10 + K(9).get(.{});\n" +
            "}\n", 79, "" },
        new object[] { "volatile_pointees",
            "fn wipe(s: []volatile u8) void {\n" +
            "    @memset(s, 0);\n" +
            "}\n" +
            "fn bump(p: *volatile u32) void {\n" +
            "    p.* +%= 5;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var buf = [_]u8{ 9, 8, 7, 6 };\n" +
            "    wipe(buf[1..3]);\n" +
            "    var n: u32 = 40;\n" +
            "    bump(&n);\n" +
            "    return buf[0] + buf[1] + buf[2] + buf[3] + @as(u8, @intCast(n));\n" +
            "}\n", 60, "" },
        // Task #163: comptime value seeds keep their declared type as anytype arguments (width and signedness), and a type
        // argument chosen by a switch over a comptime tag keeps its width (std.math.log2 over keccak's `f / 25`).
        new object[] { "comptime_seed_anytype_types",
            "fn bitsOf(x: anytype) u16 {\n" +
            "    return @typeInfo(@TypeOf(x)).int.bits;\n" +
            "}\n" +
            "fn signOf(x: anytype) u8 {\n" +
            "    return if (@typeInfo(@TypeOf(x)).int.signedness == .unsigned) 1 else 2;\n" +
            "}\n" +
            "fn K(comptime f: u11) type {\n" +
            "    return struct {\n" +
            "        pub const b = bitsOf(f / 25);\n" +
            "        pub const r = f / 25;\n" +
            "        pub const s = signOf(f / 25);\n" +
            "    };\n" +
            "}\n" +
            "fn twice(comptime n: u5) u16 {\n" +
            "    return bitsOf(n) * 2 + signOf(n);\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return @intCast(K(1600).b * 10 + K(1600).r % 10 + twice(3) + K(1600).s);\n" +
            "}\n", 126, "" },
        new object[] { "log2_over_a_seeded_u11",
            "fn Log2Int(comptime T: type) type {\n" +
            "    const bits: u16 = @typeInfo(T).int.bits;\n" +
            "    const log2_bits = 16 - @clz(bits - 1);\n" +
            "    return @Int(.unsigned, log2_bits);\n" +
            "}\n" +
            "fn log2Int(comptime T: type, x: T) Log2Int(T) {\n" +
            "    return @intCast(@typeInfo(T).int.bits - 1 - @clz(x));\n" +
            "}\n" +
            "fn log2(x: anytype) @TypeOf(x) {\n" +
            "    const T = @TypeOf(x);\n" +
            "    return switch (@typeInfo(T)) {\n" +
            "        .int => |int_info| log2Int(switch (int_info.signedness) {\n" +
            "            .signed => @Int(.unsigned, int_info.bits -| 1),\n" +
            "            .unsigned => T,\n" +
            "        }, @intCast(x)),\n" +
            "        else => @compileError(\"log2 of a non-integer\"),\n" +
            "    };\n" +
            "}\n" +
            "fn K(comptime f: u11) type {\n" +
            "    return struct {\n" +
            "        pub const max_rounds = 12 + 2 * log2(f / 25);\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return K(1600).max_rounds;\n" +
            "}\n", 24, "" },
        // Task #164: an instance reified while another container's const lowers (keccak_p's State `buf: [rate]u8`).
        new object[] { "reify_inside_other_container_const",
            "fn F(comptime f: u11) type {\n" +
            "    return struct {\n" +
            "        const Self = @This();\n" +
            "        pub const block_bytes = f / 8;\n" +
            "        st: [block_bytes]u8 = @splat(0),\n" +
            "        pub fn init(bytes: [block_bytes]u8) Self {\n" +
            "            var self: Self = undefined;\n" +
            "            self.st = bytes;\n" +
            "            return self;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "fn State(comptime f: u11, comptime capacity: u11) type {\n" +
            "    return struct {\n" +
            "        const Self = @This();\n" +
            "        pub const rate = F(f).block_bytes - capacity / 8;\n" +
            "        buf: [rate]u8 = undefined,\n" +
            "        st: F(f) = .{},\n" +
            "        pub fn init(bytes: [f / 8]u8) Self {\n" +
            "            return Self{ .st = F(f).init(bytes) };\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var bytes: [5]u8 = @splat(0);\n" +
            "    bytes[0] = 7;\n" +
            "    const s = State(40, 16).init(bytes);\n" +
            "    return @as(u8, @intCast(State(40, 16).rate)) + s.st.st[0];\n" +
            "}\n", 10, "" },
        // Task #166: `@splat` of a runtime value into an array (a return, a local, an argument), its element evaluated once.
        new object[] { "runtime_array_splat",
            "var calls: u8 = 0;\n" +
            "fn next() u8 {\n" +
            "    calls += 1;\n" +
            "    return calls * 3;\n" +
            "}\n" +
            "fn sum(v: [4]u8) u32 {\n" +
            "    var t: u32 = 0;\n" +
            "    for (v) |x| t += x;\n" +
            "    return t;\n" +
            "}\n" +
            "fn fill(b: u8) [5]u8 {\n" +
            "    return @splat(b);\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const a = fill(7);\n" +
            "    var local: [3]u16 = @splat(@as(u16, a[2]) + 1);\n" +
            "    local[0] = 1;\n" +
            "    const s = sum(@splat(next()));\n" +
            "    return @intCast(a[0] + a[4] + local[0] + local[2] + s + calls);\n" +
            "}\n", 36, "" },
        // Task #165: integer coercions zig allows at a return and a typed declaration (every one a widening).
        new object[] { "integer_widening_coercions",
            "fn widen(x: u8) u16 {\n" +
            "    return x;\n" +
            "}\n" +
            "fn toSigned(x: u8) i16 {\n" +
            "    return x;\n" +
            "}\n" +
            "fn sext(x: i8) i32 {\n" +
            "    return x;\n" +
            "}\n" +
            "fn bump(x: u8) u8 {\n" +
            "    return x +% 1;\n" +
            "}\n" +
            "fn mix(a: u8, b: u16) u32 {\n" +
            "    const w: u32 = a + b;\n" +
            "    return w;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const s = sext(-3);\n" +
            "    return @intCast(widen(200) - 100 + @as(u16, @intCast(toSigned(7))) + @as(u16, @intCast(s + 10)) + bump(255) + mix(1, 2));\n" +
            "}\n", 117, "" },
        // Task #167: integer coercions zig allows into an argument, a field, an element and a local, and `usize + u8`.
        new object[] { "integer_widening_stores",
            "const S = struct { w: u32, b: [2]u16 };\n" +
            "fn take(x: u32) u32 {\n" +
            "    return x;\n" +
            "}\n" +
            "fn count() usize {\n" +
            "    return 5;\n" +
            "}\n" +
            "fn f(a: u8) u8 {\n" +
            "    var s = S{ .w = 0, .b = .{ 0, 0 } };\n" +
            "    s.w = a;\n" +
            "    s.b[1] = a;\n" +
            "    var l: u16 = 0;\n" +
            "    l = a;\n" +
            "    const n = count() + a;\n" +
            "    return @intCast(take(a) + s.w + s.b[1] + l + n);\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return f(7);\n" +
            "}\n", 40, "" },
        // Task #168: `create` on a namespace struct's type const (std.crypto.hmac's `sha2.HmacSha256`), and a comptime_int
        // slice bound.
        new object[] { "namespace_type_const_create",
            "const ns = struct {\n" +
            "    pub const HB = H(u8);\n" +
            "};\n" +
            "fn H(comptime T: type) type {\n" +
            "    return struct {\n" +
            "        pub fn create(out: *T, x: T) void {\n" +
            "            out.* = x + 1;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var o: u8 = 0;\n" +
            "    ns.HB.create(&o, 4);\n" +
            "    return o;\n" +
            "}\n", 5, "" },
        new object[] { "comptime_int_slice_bound",
            "fn H(comptime bits: comptime_int) type {\n" +
            "    return struct {\n" +
            "        pub const len = bits / 8;\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var scratch: [8]u8 = .{ 1, 2, 3, 4, 5, 6, 7, 8 };\n" +
            "    const tail = scratch[H(32).len..];\n" +
            "    @memset(scratch[0..H(32).len], 0);\n" +
            "    return tail[0] + @as(u8, @intCast(tail.len)) + scratch[0];\n" +
            "}\n", 9, "" },
        // Task #170: `a ++ if (c) x else y` as a whole right-hand side, with a runtime and a comptime condition.
        new object[] { "concat_if_operand",
            "fn pick(upper: bool) u8 {\n" +
            "    const charset = \"0123456789\" ++ if (upper) \"ABCDEF\" else \"abcdef\";\n" +
            "    return charset[12];\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const fixed = \"ab\" ++ if (true) \"cd\" else \"ef\";\n" +
            "    return pick(false) - pick(true) + fixed[3];\n" +
            "}\n", 132, "" },
        // A call through a fn-pointer FIELD, on a value and through a pointer (std.Io.Writer's
        // `w.vtable.drain(…)` dispatch shape).
        new object[] { "fn_pointer_field_call",
            "const Ops = struct {\n" +
            "    f: *const fn (x: u8) u8,\n" +
            "};\n" +
            "fn twice(x: u8) u8 {\n" +
            "    return x * 2;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const o = Ops{ .f = twice };\n" +
            "    const p = &o;\n" +
            "    return o.f(20) + p.f(1);\n" +
            "}\n", 42, "" },
        // An unlabeled `break` in a switch prong exits the LOOP (a C# `break` there would exit only the
        // switch, and the loop kept iterating: dotcc returned 90). i = 3, n = 10 * 2 = 20.
        new object[] { "switch_break_exits_loop",
            "pub fn main() u8 {\n" +
            "    var i: u8 = 0;\n" +
            "    var n: u8 = 0;\n" +
            "    while (i < 10) : (i += 1) {\n" +
            "        switch (i) {\n" +
            "            1 => {\n" +
            "                continue;\n" +
            "            },\n" +
            "            3 => {\n" +
            "                break;\n" +
            "            },\n" +
            "            else => {},\n" +
            "        }\n" +
            "        n += 10;\n" +
            "    }\n" +
            "    return i + n;\n" +
            "}\n", 23, "" },
        // Bare JUMP prong bodies (std.Io.Writer.print's `'{', '}' => break,`): `continue`, `continue :l`,
        // `break` in a labeled loop, `break :blk v`, and `break` / `continue` in a `catch |e| switch`.
        // i = 7, n = 5 (0, 3, 4, 5, 6), v = 24, k = 3, sum = 1 + 0 + 2 = 3: 42.
        new object[] { "switch_jump_prongs",
            "fn f(x: u8) error{ A, B }!u8 {\n" +
            "    if (x == 3) return error.A;\n" +
            "    if (x == 1) return error.B;\n" +
            "    return x;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var i: u8 = 0;\n" +
            "    var n: u8 = 0;\n" +
            "    outer: while (i < 20) : (i += 1) {\n" +
            "        switch (i) {\n" +
            "            1 => continue,\n" +
            "            2 => continue :outer,\n" +
            "            7 => break,\n" +
            "            else => {},\n" +
            "        }\n" +
            "        n += 1;\n" +
            "    }\n" +
            "    const v: u8 = blk: {\n" +
            "        switch (n) {\n" +
            "            5 => break :blk 24,\n" +
            "            else => break :blk 0,\n" +
            "        }\n" +
            "    };\n" +
            "    var k: u8 = 0;\n" +
            "    var sum: u8 = 1;\n" +
            "    while (k < 10) : (k += 1) {\n" +
            "        const x = f(k) catch |e| switch (e) {\n" +
            "            error.A => break,\n" +
            "            error.B => continue,\n" +
            "        };\n" +
            "        sum += x;\n" +
            "    }\n" +
            "    return i + n + v + k + sum;\n" +
            "}\n", 42, "" },
    };

    private static string Norm(string s) => s.ReplaceLineEndings("\n").TrimEnd('\n');

    [Theory]
    [MemberData(nameof(Programs))]
    public void Dotcc_matches_zig(string name, string program, int expectedExit, string expectedOutput)
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
        string dotccStdout, dotccStderr;
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { zigPath }, emit: EmitMode.Csproj);
            // Capture BOTH streams: `std.debug.print` (wall-plan W6) — like real Zig — writes to stderr,
            // not stdout, so an stdout-only assertion would pass vacuously against it.
            (dotccStdout, dotccStderr, dotccExit) = FixtureRunner.CompileAndRunCapturingStreams(emitted, Array.Empty<string>());
        }
        finally { File.Delete(zigPath); }

        // zig path: build + run the SAME source with the real compiler.
        var workDir = Path.Combine(Path.GetTempPath(), $"dotcc-zig-oracle-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var zigSrc = Path.Combine(workDir, "main.zig");
        File.WriteAllText(zigSrc, program);
        try
        {
            var (zigStdout, zigStderr, zigExit) = ZigOracle.CompileAndRunStreams(zigSrc, workDir);

            dotccExit.ShouldBe(zigExit, $"dotcc's Zig path diverges from real zig on '{name}' (exit code)");
            dotccExit.ShouldBe(expectedExit, $"'{name}' did not produce the expected exit code");
            Norm(dotccStdout).ShouldBe(Norm(zigStdout), $"dotcc's Zig path diverges from real zig on '{name}' (stdout)");

            // STDERR is compared ONLY on the success path (exit 0) — that's where `std.debug.print`
            // output lives (wall-plan W6). On an error-exit, real Zig dumps a debug stack trace to
            // stderr (absolute source paths + addresses + an unwind) that is inherently non-reproducible
            // and that dotcc deliberately does NOT replicate (it maps an unhandled error to a clean exit
            // 1, stdout untouched), so stderr is not comparable there — only the exit code + stdout are.
            // The committed expected output is therefore the combined stdout+stderr on success, or just
            // stdout on an error-exit (each stream captured separately and concatenated in a fixed order,
            // so the comparison is order-stable; no oracle program interleaves the two).
            if (zigExit == 0)
            {
                Norm(dotccStderr).ShouldBe(Norm(zigStderr), $"dotcc's Zig path diverges from real zig on '{name}' (stderr)");
            }
            var actual = zigExit == 0 ? dotccStdout + dotccStderr : dotccStdout;
            Norm(actual).ShouldBe(expectedOutput, $"'{name}' did not produce the expected output");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>MULTI-FILE Zig programs (road-to-zig-std S1 — the module graph). Each case is a root
    /// <c>main.zig</c> that <c>@import</c>s a sibling by relative path + the sibling's source + expected
    /// exit + stdout. dotcc is given ONLY the root and discovers the sibling through the module graph,
    /// exactly as <c>zig build-exe main.zig</c> does; the differential proves the imported module lowers
    /// into the same program and cross-module calls resolve.</summary>
    /// <remarks>This host's rows of <see cref="AllMultiFilePrograms"/> (<see cref="TestShard"/>).</remarks>
    public static IEnumerable<object[]> MultiFilePrograms => TestShard.Rows(AllMultiFilePrograms);

    private static IEnumerable<object[]> AllMultiFilePrograms => new[]
    {
        // std.Target's shape (road-to-zig-std, the target-identity segment T1/T2): a module-qualified NESTED
        // type (`tgt.Cpu.Arch`, `tgt.Cpu.Arch.Family`: the module prefix, then the owner's nested containers)
        // and a container nested in an ENUM body, with methods (`arch.family()`). 64 - 16 - 38 + 10 + 22 = 42.
        new object[] { "module_nested_enum_types",
            "const tgt = @import(\"tgt.zig\");\n" +
            "pub fn main() u8 {\n" +
            "    const a: tgt.Cpu.Arch = .aarch64;\n" +
            "    const f: tgt.Cpu.Arch.Family = a.family();\n" +
            "    const c: tgt.Cpu = .{ .arch = .avr };\n" +
            "    const fam_bonus: u8 = if (f == .arm) 10 else 0;\n" +
            "    return a.ptrBits() - c.arch.ptrBits() - 38 + fam_bonus + 22;\n" +
            "}\n",
            "tgt.zig",
            "pub const Cpu = struct {\n" +
            "    arch: Arch,\n" +
            "    pub const Arch = enum {\n" +
            "        x86_64,\n" +
            "        aarch64,\n" +
            "        avr,\n" +
            "        pub const Family = enum { x86, arm, avr };\n" +
            "        pub fn family(arch: Arch) Family {\n" +
            "            return switch (arch) {\n" +
            "                .x86_64 => .x86,\n" +
            "                .aarch64 => .arm,\n" +
            "                .avr => .avr,\n" +
            "            };\n" +
            "        }\n" +
            "        pub fn ptrBits(arch: Arch) u8 {\n" +
            "            return switch (arch.family()) {\n" +
            "                .avr => 16,\n" +
            "                else => 64,\n" +
            "            };\n" +
            "        }\n" +
            "    };\n" +
            "};\n", 42, "" },
        // std.fmt.Placeholder.parse's shapes, in an imported (lazy) module: `.{ .none = {} }` for a void variant, a
        // `!Spec` literal return, an untyped `const default_mode = .right;` read at a field default and after
        // `orelse`, a value `if (r.peek()) |c| blk: {...} else null` at a `?Mode` sink, a value switch over a union
        // with a `|v|` capture prong, and `catch unreachable`. (20 + 10) + (1 + 0) + 11 = 42.
        new object[] { "fmt_parse_shapes",
            "const p = @import(\"spec.zig\");\n" +
            "pub fn main() u8 {\n" +
            "    const a = p.parse(\"5<\");\n" +
            "    const b = p.parse(\"x\");\n" +
            "    return a.total() + b.total() + 11;\n" +
            "}\n",
            "spec.zig",
            "const default_mode = .right;\n" +
            "pub const Mode = enum { left, center, right };\n" +
            "pub const Spec = union(enum) { none, number: u8 };\n" +
            "const Reader = struct {\n" +
            "    bytes: []const u8,\n" +
            "    i: usize,\n" +
            "    fn peek(self: *Reader) ?u8 {\n" +
            "        return if (self.i < self.bytes.len) self.bytes[self.i] else null;\n" +
            "    }\n" +
            "    fn number(self: *Reader) !Spec {\n" +
            "        if (self.peek()) |c| {\n" +
            "            if (c >= '0' and c <= '9') {\n" +
            "                self.i += 1;\n" +
            "                return .{ .number = c - '0' };\n" +
            "            }\n" +
            "        }\n" +
            "        return .{ .none = {} };\n" +
            "    }\n" +
            "};\n" +
            "pub const Result = struct {\n" +
            "    spec: Spec,\n" +
            "    mode: Mode = default_mode,\n" +
            "    pub fn total(self: Result) u8 {\n" +
            "        const n: u8 = switch (self.spec) {\n" +
            "            .none => 1,\n" +
            "            .number => |v| v * 4,\n" +
            "        };\n" +
            "        const m: u8 = switch (self.mode) {\n" +
            "            .left => 10,\n" +
            "            .center => 20,\n" +
            "            .right => 0,\n" +
            "        };\n" +
            "        return n + m;\n" +
            "    }\n" +
            "};\n" +
            "pub fn parse(bytes: []const u8) Result {\n" +
            "    var r: Reader = .{ .bytes = bytes, .i = 0 };\n" +
            "    const spec = r.number() catch unreachable;\n" +
            "    const mode: ?Mode = if (r.peek()) |c| blk: {\n" +
            "        switch (c) {\n" +
            "            '<' => break :blk .left,\n" +
            "            '^' => break :blk .center,\n" +
            "            else => break :blk null,\n" +
            "        }\n" +
            "    } else null;\n" +
            "    return .{ .spec = spec, .mode = mode orelse default_mode };\n" +
            "}\n", 42, "" },
        // Module-qualified type paths from user code: a static call through `h.H` (`h.H.hash(0, 0)`) and a type
        // alias rooted at an inline import (`const H2 = @import("h.zig").H;`). 42 + 41 - 41 = 42.
        new object[] { "module_qualified_type_paths",
            "const h = @import(\"h.zig\");\n" +
            "const H2 = @import(\"h.zig\").H;\n" +
            "pub fn main() u8 {\n" +
            "    const a = h.H.hash(0, 0);\n" +
            "    const b: H2 = H2.init(1);\n" +
            "    return @intCast(a + b.a - 41);\n" +
            "}\n",
            "h.zig",
            "pub const H = struct {\n" +
            "    const secret = [_]u64{ 40, 2 };\n" +
            "    a: u64,\n" +
            "    pub fn init(seed: u64) H {\n" +
            "        return .{ .a = seed ^ secret[0] };\n" +
            "    }\n" +
            "    pub fn hash(seed: u64, n: u64) u64 {\n" +
            "        const h = H.init(seed);\n" +
            "        return h.a + n + secret[1];\n" +
            "    }\n" +
            "};\n", 42, "" },
        // A method declared ON DEMAND in the middle of another body (a lazy module's `H.init(seed)` inside
        // `H.hash`) must not clear that body's container scope: its next bare container const (`secret[1]`)
        // resolved to nothing (std.hash.Wyhash's shape). 40 ^ 0 + 0 + 2 = 42.
        new object[] { "lazy_method_container_scope",
            "const hmod = @import(\"h.zig\");\n" +
            "const H = hmod.H;\n" +
            "pub fn main() u8 {\n" +
            "    return @intCast(H.hash(0, 0));\n" +
            "}\n",
            "h.zig",
            "pub const H = struct {\n" +
            "    const secret = [_]u64{ 40, 2 };\n" +
            "    a: u64,\n" +
            "    pub fn init(seed: u64) H {\n" +
            "        return .{ .a = seed ^ secret[0] };\n" +
            "    }\n" +
            "    pub fn hash(seed: u64, n: u64) u64 {\n" +
            "        const h = H.init(seed);\n" +
            "        return h.a + n + secret[1];\n" +
            "    }\n" +
            "};\n", 42, "" },
        // A lazily prepared module's top-level CALL consts are evaluated only when named (zig analyses a
        // declaration on reference): `Unused = Gen(.{ .width = 7 })` takes a comptime STRUCT argument dotcc
        // cannot evaluate, and it used to sink the module's preparation, losing `Bytes` too. 40 + 2 = 42.
        new object[] { "deferred_type_calls",
            "const gen = @import(\"gen.zig\");\n" +
            "pub fn main() u8 {\n" +
            "    const p: gen.Bytes = .{ .a = 40, .b = 2 };\n" +
            "    return p.a + p.b;\n" +
            "}\n",
            "gen.zig",
            "pub const Options = struct { width: u8 };\n" +
            "pub fn Gen(comptime opts: Options) type {\n" +
            "    return struct { v: @Int(.unsigned, opts.width) };\n" +
            "}\n" +
            "pub fn Pair(comptime T: type) type {\n" +
            "    return struct { a: T, b: T };\n" +
            "}\n" +
            "pub const Unused = Gen(.{ .width = 7 });\n" +
            "pub const Bytes = Pair(u8);\n", 42, "" },
        // array_list's surface (road-to-zig-std G4): a decl literal VALUE (`.empty`) of a container the
        // SIBLING declares is lowered by that module, in the container's scope (`empty: Self`); the empty
        // slice spelled `&.{}` and `&[_]T{}`; a bare sibling call (`self.* = fromEmpty();`); and `@memmove`
        // over overlapping operands. 0 + 0 + (2 + 30 + 4 + 4) + 2 = 42.
        new object[] { "cross_module_decl_literal",
            "const list = @import(\"list.zig\");\n" +
            "pub fn main() u8 {\n" +
            "    var a: list.List(u8) = .empty;\n" +
            "    a.used = 5;\n" +
            "    a.reset();\n" +
            "    var buf = [_]u8{ 1, 2, 30, 4 };\n" +
            "    list.List(u8).shiftLeft(&buf);\n" +
            "    return @intCast(a.items.len + a.used + buf[0] + buf[1] + buf[2] + buf[3] + 2);\n" +
            "}\n",
            "list.zig",
            "pub fn List(comptime T: type) type {\n" +
            "    return struct {\n" +
            "        items: []T,\n" +
            "        used: usize,\n" +
            "        const Self = @This();\n" +
            "        pub const empty: Self = .{ .items = &.{}, .used = 0 };\n" +
            "        pub fn fromEmpty() Self {\n" +
            "            return .{ .items = &[_]T{}, .used = 0 };\n" +
            "        }\n" +
            "        pub fn reset(self: *Self) void {\n" +
            "            self.* = fromEmpty();\n" +
            "        }\n" +
            "        pub fn shiftLeft(buf: []T) void {\n" +
            "            @memmove(buf[0 .. buf.len - 1], buf[1..]);\n" +
            "        }\n" +
            "    };\n" +
            "}\n", 42, "" },
        // Module-qualified container naming: the root AND the imported module each declare a `Shape`
        // struct and a `Kind` enum with DIFFERENT layouts/members. The emitted C# carries one type per
        // name, so this used to be a loud "two different aggregates" error (and, for the enum, a silent
        // first-definition-wins); the import's containers now emit as `shapes__Shape` / `shapes__Kind`.
        // 30 + 10 + 2 = 42.
        new object[] { "import_same_named_types",
            "const shapes = @import(\"shapes.zig\");\n" +
            "const Shape = struct { w: u8 };\n" +
            "const Kind = enum { a, b };\n" +
            "pub fn main() u8 {\n" +
            "    const mine: Shape = .{ .w = 30 };\n" +
            "    const theirs: shapes.Shape = .{ .h = 10, .d = 2 };\n" +
            "    const k: Kind = .b;\n" +
            "    const t: shapes.Kind = .z;\n" +
            "    if (k != .b or t != .z) return 1;\n" +
            "    return mine.w + theirs.h + theirs.d;\n" +
            "}\n",
            "shapes.zig",
            "pub const Shape = struct { h: u8, d: u8 };\n" +
            "pub const Kind = enum { x, y, z };\n", 42, "" },
        // main imports a sibling and calls its exported fn: add(40, 2) = 42.
        new object[] { "import_call",
            "const util = @import(\"util.zig\");\npub fn main() u8 { return util.add(40, 2); }\n",
            "util.zig", "pub fn add(a: u8, b: u8) u8 { return a + b; }\n", 42, "" },
        // A sibling that itself imports a THIRD module (transitive import), and a call chain across all
        // three: main → a.step (→ b.two) ; 40 + 2 = 42.
        new object[] { "import_transitive",
            "const a = @import(\"a.zig\");\npub fn main() u8 { return a.step(40); }\n",
            "a.zig", "const b = @import(\"b.zig\");\npub fn step(x: u8) u8 { return x + b.two(); }\n", 42, "" },
        // A TYPE declared in the imported module, used in an annotation (road-to-zig-std S4d):
        // 40 + 2 = 42. Only functions crossed the module seam before.
        new object[] { "import_type",
            "const util = @import(\"util.zig\");\n" +
            "pub fn main() u8 { const p: util.Point = .{ .x = 40, .y = 2 }; return p.x + p.y; }\n",
            "util.zig", "pub const Point = struct { x: u8, y: u8 };\n", 42, "" },
        // A METHOD on a type the imported module declares — declared on demand at the call site and
        // lowered in its own module (S4d): 40 + 2 = 42.
        new object[] { "import_type_method",
            "const geom = @import(\"geom.zig\");\n" +
            "pub fn main() u8 { const p: geom.Point = .{ .x = 40, .y = 2 }; return p.sum(); }\n",
            "geom.zig",
            "pub const Point = struct {\n" +
            "    x: u8,\n" +
            "    y: u8,\n" +
            "    pub fn sum(self: Point) u8 { return self.x + self.y; }\n" +
            "};\n", 42, "" },
        // An ENUM the imported module declares, with a bare `.member` literal at an imported-enum sink.
        new object[] { "import_enum_member",
            "const geom = @import(\"geom.zig\");\n" +
            "pub fn main() u8 {\n" +
            "    const a: geom.Axis = .y;\n" +
            "    return if (a == .y) 42 else 0;\n" +
            "}\n",
            "geom.zig", "pub const Axis = enum { x, y };\n", 42, "" },
        // A type-returning GENERIC declared in the imported module, instantiated in a type slot and
        // called through — the shape `std.ArrayList(T)` has (S4d × G4): 40 + 2 = 42.
        new object[] { "import_generic_type",
            "const list = @import(\"list.zig\");\n" +
            "pub fn main() u8 {\n" +
            "    var box: list.Box(u8) = .{ .first = 2, .len = 40 };\n" +
            "    box.len += 0;\n" +
            "    return @intCast(box.total());\n" +
            "}\n",
            "list.zig",
            "pub fn Box(comptime T: type) type {\n" +
            "    return struct {\n" +
            "        first: T,\n" +
            "        len: usize,\n" +
            "        const Self = @This();\n" +
            "        pub fn total(self: Self) usize { return self.len + self.first; }\n" +
            "    };\n" +
            "}\n", 42, "" },
        // GENERIC functions declared in the imported module — each monomorphization-key kind, with the
        // arguments spelled in the CALLER (a caller-side type alias, a caller-side named `const`, a
        // caller-side comptime string): a `comptime T: type`, a `comptime n: u8`, an `anytype`, and a
        // `comptime s: []const u8` (the `std.fmt.bufPrint` shape — road-to-zig-std G3). The call used to
        // bind the template's empty placeholder signature ("expected 0 argument(s), got 3").
        // 10 + 23 + 6 + 3 = 42.
        new object[] { "import_generic_fn",
            "const util = @import(\"util.zig\");\n" +
            "const N: u8 = 20;\n" +
            "const S = \"abc\";\n" +
            "pub fn main() u8 {\n" +
            "    const T = u8;\n" +
            "    const m = util.maxOf(T, 10, 7);\n" +
            "    const a = util.addN(N, 3);\n" +
            "    const t = util.twice(@as(u8, 3));\n" +
            "    const l = util.lenOf(S);\n" +
            "    if (l != util.lenOf(\"xyz\")) return 1;\n" +
            "    return m + a + t + l;\n" +
            "}\n",
            "util.zig",
            "pub fn maxOf(comptime T: type, a: T, b: T) T {\n" +
            "    return if (a > b) a else b;\n" +
            "}\n" +
            "pub fn addN(comptime n: u8, x: u8) u8 {\n" +
            "    return x + n;\n" +
            "}\n" +
            "pub fn twice(x: anytype) @TypeOf(x) {\n" +
            "    return x + x;\n" +
            "}\n" +
            "pub fn lenOf(comptime s: []const u8) u8 {\n" +
            "    return @intCast(s.len);\n" +
            "}\n", 42, "" },
        // A DECL LITERAL on a container the IMPORTED module declares — the function resolves on the
        // result type and lowers in its own module: 40 + 2 = 42.
        new object[] { "import_decl_literal",
            "const geom = @import(\"geom.zig\");\n" +
            "pub fn main() u8 {\n" +
            "    const p: geom.Point = .init(40, 2);\n" +
            "    return p.sum();\n" +
            "}\n",
            "geom.zig",
            "pub const Point = struct {\n" +
            "    x: u8,\n" +
            "    y: u8,\n" +
            "    pub fn init(x: u8, y: u8) Point {\n" +
            "        return .{ .x = x, .y = y };\n" +
            "    }\n" +
            "    pub fn sum(self: Point) u8 {\n" +
            "        return self.x + self.y;\n" +
            "    }\n" +
            "};\n", 42, "" },
        // A FILE used as a struct type (road-to-zig-std G3, `std.Io.Writer`'s shape): top-level fields
        // make Box.zig the `Box` type, and its top-level functions its methods, reached by a decl
        // literal, a static call and a method call. The root's own `init(u8) u8` sits beside Box's
        // `init(u8) Box`, which emitted as two C# `init(byte)` methods before imported functions were
        // module-qualified. b: 30 + 11 = 41, get = 42; c: 0, get = 1; 42 + 1 - init(1) = 42.
        new object[] { "import_file_struct",
            "const Box = @import(\"Box.zig\");\n" +
            "fn init(v: u8) u8 {\n" +
            "    return v;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var b: Box = .init(30);\n" +
            "    b.add(11);\n" +
            "    var c = Box.init(0);\n" +
            "    c.add(b.get() - 42);\n" +
            "    return b.get() + c.get() - init(1);\n" +
            "}\n",
            "Box.zig",
            "const Box = @This();\n" +
            "\n" +
            "value: u8,\n" +
            "bump: u8 = 1,\n" +
            "\n" +
            "pub fn init(v: u8) Box {\n" +
            "    return .{ .value = v };\n" +
            "}\n" +
            "\n" +
            "pub fn get(b: *const Box) u8 {\n" +
            "    return b.value + b.bump;\n" +
            "}\n" +
            "\n" +
            "pub fn add(b: *Box, n: u8) void {\n" +
            "    b.value += n;\n" +
            "}\n", 42, "" },
        // A GENERIC top-level function of a file-as-struct module called on an instance, the shape of
        // std.Io.Writer's `w.print(fmt, args)` (road-to-zig-std G3): a comptime value and an `anytype`
        // argument instantiate it, and an error-union generic composes with `catch`.
        // 30 + 4 + 6 + 0 + 2 = 42.
        new object[] { "file_struct_generic_method",
            "const Writer = @import(\"Writer.zig\");\n" +
            "pub fn main() u8 {\n" +
            "    var w: Writer = .{};\n" +
            "    w.put(30, @as(u8, 4));\n" +
            "    w.put(6, @as(u16, 0));\n" +
            "    w.putStr(\"ab\") catch return 1;\n" +
            "    return w.total;\n" +
            "}\n",
            "Writer.zig",
            "const Writer = @This();\n" +
            "\n" +
            "total: u8 = 0,\n" +
            "\n" +
            "pub fn put(w: *Writer, comptime n: u8, x: anytype) void {\n" +
            "    w.total += n + @as(u8, @intCast(x));\n" +
            "}\n" +
            "\n" +
            "pub fn putStr(w: *Writer, comptime s: []const u8) error{Full}!void {\n" +
            "    if (w.total > 200) return error.Full;\n" +
            "    w.total += @intCast(s.len);\n" +
            "}\n", 42, "" },
        // Comptime value forms on std.Io.Writer.print's path (road-to-zig-std G3): `comptime switch` /
        // `comptime if` in value position; an `enum(u64)` member of `maxInt(u64)` (std.Io.Limit's
        // `unlimited`), which needs the comptime_int call evaluated during registration and a member value
        // above long.MaxValue; and a statement `unreachable` prong. 30 + 2 + 5 + 4 + 1 = 42.
        new object[] { "comptime_value_forms",
            "const m = @import(\"m.zig\");\n" +
            "const Limit = enum(u64) {\n" +
            "    nothing = 0,\n" +
            "    unlimited = m.maxInt(u64),\n" +
            "    _,\n" +
            "};\n" +
            "fn pick(comptime k: u8) u8 {\n" +
            "    const v = comptime switch (k) {\n" +
            "        1 => 30,\n" +
            "        else => 0,\n" +
            "    };\n" +
            "    const w: u8 = comptime if (k == 1) 2 else 0;\n" +
            "    return v + w;\n" +
            "}\n" +
            "fn check(x: u8) u8 {\n" +
            "    switch (x) {\n" +
            "        0 => unreachable,\n" +
            "        else => {},\n" +
            "    }\n" +
            "    return x;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const l: Limit = .unlimited;\n" +
            "    const big: u64 = @intFromEnum(l);\n" +
            "    var r: u8 = pick(1);\n" +
            "    if (big == 18446744073709551615) r += 5;\n" +
            "    if (l != .nothing) r += 4;\n" +
            "    return r + check(1);\n" +
            "}\n",
            "m.zig",
            "pub fn maxInt(comptime T: type) comptime_int {\n" +
            "    const info = @typeInfo(T).int;\n" +
            "    return (1 << (info.bits - @intFromBool(info.signedness == .signed))) - 1;\n" +
            "}\n", 42, "" },
        // std.Io.Writer.fixed's shape: `.vtable = &.{ .drain = fixedDrain }` puts a comptime-known literal in
        // STATIC storage (both writers share one vtable address), its unset field takes its default, and
        // the lazy module's `fixedDrain` is a function named as a VALUE. 40 + 1 + 1 = 42.
        // The prelude of std.Io.Writer.print: a module-qualified type ALIAS (`std.fmt.ArgSetType = u32`)
        // with its declared width, a local `const` that folds so the `@compileError` guard prunes, and
        // `@as(comptime_int, n)` in `@setEvalBranchQuota`. 32 + 2 + 8 = 42.
        new object[] { "print_prelude",
            "const m = @import(\"m.zig\");\n" +
            "fn check(comptime n: usize) u8 {\n" +
            "    const max = @typeInfo(m.Word).int.bits;\n" +
            "    if (n > max) {\n" +
            "        @compileError(\"too many\");\n" +
            "    }\n" +
            "    @setEvalBranchQuota(@as(comptime_int, n) * 1000);\n" +
            "    return @intCast(max + n);\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const w: m.Word = 8;\n" +
            "    return check(2) + @as(u8, @intCast(w));\n" +
            "}\n",
            "m.zig",
            "pub const Word = u32;\n", 42, "" },
        // A LAZY module's top-level value consts, named from a function body (mem.zig's
        // `use_vectors_for_comparison = use_vectors and !builtin.fuzz`, road-to-zig-std G5): a chain of
        // consts over an enum switch whose case list ends in a trailing comma, and `@inComptime()`.
        new object[] { "lazy_value_consts",
            "const m = @import(\"m.zig\");\n" +
            "pub fn main() u8 {\n" +
            "    return m.get();\n" +
            "}\n",
            "m.zig",
            "const Mode = enum { fast, small, tiny };\n" +
            "const mode: Mode = .fast;\n" +
            "const wide = switch (mode) {\n" +
            "    .small,\n" +
            "    .tiny,\n" +
            "    => false,\n" +
            "    else => true,\n" +
            "};\n" +
            "const use_wide = wide and !false;\n" +
            "const base: u8 = 40;\n" +
            "pub fn get() u8 {\n" +
            "    if (!@inComptime() and use_wide) return base + 2;\n" +
            "    return base;\n" +
            "}\n", 42, "" },
        // std.fmt.parseIntWithSign's shapes (road-to-zig-std G3): a local comptime alias of another module's
        // GENERIC function picked by a comptime switch (`const add = switch (sign) { .pos => math.add, … }`),
        // a parenthesized error-union return type, a comptime bool from a TYPE comparison, a value `if` on
        // one, a value switch with a `return` arm, variadic `@max`, and a lazy module's helper named from two
        // different bodies. 14 + 7 + 5 + 7 + 4 + 1 + 2 + 5 - 3 = 42.
        new object[] { "fn_alias_and_comptime_values",
            "const m = @import(\"m.zig\");\n" +
            "fn apply(comptime sign: enum { pos, neg }, a: u8, b: u8) u8 {\n" +
            "    const op = switch (sign) {\n" +
            "        .pos => m.add,\n" +
            "        .neg => m.sub,\n" +
            "    };\n" +
            "    return op(u8, a, b) catch 0;\n" +
            "}\n" +
            "fn pick(comptime T: type, x: T) u8 {\n" +
            "    const is_byte = T == u8;\n" +
            "    if (!is_byte) return 0;\n" +
            "    return if (T == u8) x else 0;\n" +
            "}\n" +
            "fn digit(c: u8) error{Bad}!u8 {\n" +
            "    const v = switch (c) {\n" +
            "        '0'...'9' => c - '0',\n" +
            "        else => return error.Bad,\n" +
            "    };\n" +
            "    return v;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const n: u8 = @max(3, 7, 5);\n" +
            "    const d = digit('4') catch 0;\n" +
            "    const bad = digit('x') catch 1;\n" +
            "    return apply(.pos, 10, 4) + apply(.neg, 9, 2) + pick(u8, 5) + n + d + bad + m.first(1) + m.second(2) - 3;\n" +
            "}\n",
            "m.zig",
            "pub fn add(comptime T: type, a: T, b: T) (error{Overflow}!T) {\n" +
            "    const r = @addWithOverflow(a, b);\n" +
            "    if (r[1] != 0) return error.Overflow;\n" +
            "    return r[0];\n" +
            "}\n" +
            "\n" +
            "pub fn sub(comptime T: type, a: T, b: T) (error{Overflow}!T) {\n" +
            "    const r = @subWithOverflow(a, b);\n" +
            "    if (r[1] != 0) return error.Overflow;\n" +
            "    return r[0];\n" +
            "}\n" +
            "\n" +
            "fn twice(x: u8) u8 {\n" +
            "    return x * 2;\n" +
            "}\n" +
            "\n" +
            "pub fn first(x: u8) u8 {\n" +
            "    return twice(x);\n" +
            "}\n" +
            "\n" +
            "pub fn second(x: u8) u8 {\n" +
            "    return twice(x) + 1;\n" +
            "}\n", 42, "" },
        new object[] { "vtable_literal",
            "const Writer = @import(\"Writer.zig\");\n" +
            "pub fn main() u8 {\n" +
            "    var a: Writer = .fixed(30);\n" +
            "    var b = Writer.fixed(0);\n" +
            "    const same: u8 = if (a.vtable == b.vtable) 1 else 0;\n" +
            "    return a.vtable.drain(&a, 10) + b.vtable.flush(&b) + same;\n" +
            "}\n",
            "Writer.zig",
            "const Writer = @This();\n" +
            "\n" +
            "vtable: *const VTable,\n" +
            "n: u8,\n" +
            "\n" +
            "pub const VTable = struct {\n" +
            "    drain: *const fn (w: *Writer, x: u8) u8,\n" +
            "    flush: *const fn (w: *Writer) u8 = noFlush,\n" +
            "};\n" +
            "\n" +
            "pub fn fixed(n: u8) Writer {\n" +
            "    return .{ .vtable = &.{ .drain = fixedDrain }, .n = n };\n" +
            "}\n" +
            "\n" +
            "fn fixedDrain(w: *Writer, x: u8) u8 {\n" +
            "    return w.n + x;\n" +
            "}\n" +
            "\n" +
            "fn noFlush(w: *Writer) u8 {\n" +
            "    _ = w;\n" +
            "    return 1;\n" +
            "}\n", 42, "" },
        // RE-EXPORTED declarations (std's `pub const indexOfScalar = findScalar;` shape, 633 in the pin):
        // a function, a generic, a type-returning generic and a container type, each named through a
        // `pub const` alias in lib.zig, plus a root alias of an imported function (no runtime global).
        // 20 + (3 + 4 + 5 + 10) = 42.
        new object[] { "import_reexports",
            "const lib = @import(\"lib.zig\");\n" +
            "const plus = lib.plus;\n" +
            "const P = lib.Pair;\n" +
            "pub fn main() u8 {\n" +
            "    const p: P = .{ .a = 3, .b = 4 };\n" +
            "    const b: lib.Crate(u8) = .{ .v = 5 };\n" +
            "    return plus(lib.largest(u8, 20, 7), p.a + p.b + b.v + lib.twice(5));\n" +
            "}\n",
            "lib.zig",
            "pub fn add(a: u8, b: u8) u8 {\n" +
            "    return a + b;\n" +
            "}\n" +
            "pub const plus = add;\n" +
            "pub fn maxOf(comptime T: type, a: T, b: T) T {\n" +
            "    return if (a > b) a else b;\n" +
            "}\n" +
            "pub const largest = maxOf;\n" +
            "pub fn Box(comptime T: type) type {\n" +
            "    return struct { v: T };\n" +
            "}\n" +
            "pub const Crate = Box;\n" +
            "const Inner = struct { a: u8, b: u8 };\n" +
            "pub const Pair = Inner;\n" +
            "fn double(x: u8) u8 {\n" +
            "    return x + x;\n" +
            "}\n" +
            "pub const twice = double;\n", 42, "" },
    };

    [Theory]
    [MemberData(nameof(MultiFilePrograms))]
    public void Dotcc_matches_zig_multifile(string name, string mainSource, string siblingName,
        string siblingSource, int expectedExit, string expectedOutput)
    {
        if (!ZigRunRequested)
        {
            Assert.Skip($"Zig oracle is opt-in. Set {RunZigEnv}=1 to run the multi-file module-graph differential.");
        }
        if (!ZigOracle.IsAvailable)
        {
            Assert.Skip($"{RunZigEnv} requested but no `zig` is on PATH on this host.");
        }

        // One work dir holds the root + its sibling(s); both pipelines consume the same files. The
        // `import_transitive` case needs a third module `b.zig` — supplied here so the chain resolves.
        var workDir = Path.Combine(Path.GetTempPath(), $"dotcc-zig-multi-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var mainPath = Path.Combine(workDir, "main.zig");
        File.WriteAllText(mainPath, mainSource);
        File.WriteAllText(Path.Combine(workDir, siblingName), siblingSource);
        if (name == "import_transitive")
        {
            File.WriteAllText(Path.Combine(workDir, "b.zig"), "pub fn two() u8 { return 2; }\n");
        }
        try
        {
            // dotcc: emit from ONLY the root; the module graph discovers the sibling(s).
            var emitted = Compiler.EmitCSharp(new[] { mainPath }, emit: EmitMode.Csproj);
            var (dotccStdout, dotccExit) = FixtureRunner.CompileAndRunCapturingExit(emitted, Array.Empty<string>());

            // zig: build + run the same root (CompileAndRun copies the sibling .zig files alongside).
            var (zigStdout, zigExit) = ZigOracle.CompileAndRun(mainPath, workDir);

            dotccExit.ShouldBe(zigExit, $"dotcc diverges from real zig on '{name}' (exit code)");
            dotccExit.ShouldBe(expectedExit, $"'{name}' did not produce the expected exit code");
            Norm(dotccStdout).ShouldBe(Norm(zigStdout), $"dotcc diverges from real zig on '{name}' (stdout)");
            Norm(dotccStdout).ShouldBe(expectedOutput, $"'{name}' did not produce the expected output");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>THE road-to-zig-std G1 milestone: compile real upstream <c>std.ascii</c> FROM SOURCE.
    /// dotcc is given only a root that does <c>@import("std")</c> and calls the ascii classifiers; it
    /// navigates <c>std.zig</c> → <c>ascii.zig</c> (the real files, parsed resiliently) and lowers ONLY
    /// the referenced classifiers (lazily), then must agree with real zig. Needs <c>DOTCC_ZIG_LIB_DIR</c>
    /// so dotcc finds the same std source the oracle's zig uses.</summary>
    [Fact]
    public void Dotcc_matches_zig_std_ascii_from_source()
    {
        if (!ZigRunRequested)
        {
            Assert.Skip($"Zig oracle is opt-in. Set {RunZigEnv}=1 to run the std.ascii-from-source differential.");
        }
        if (!ZigOracle.IsAvailable)
        {
            Assert.Skip($"{RunZigEnv} requested but no `zig` is on PATH on this host.");
        }
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTCC_ZIG_LIB_DIR")))
        {
            Assert.Skip("DOTCC_ZIG_LIB_DIR must point at the zig lib dir so dotcc navigates the real std.ascii source.");
        }

        const string program =
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    var acc: u8 = 0;\n" +
            "    if (std.ascii.isDigit('7')) acc += 1;\n" +
            "    if (std.ascii.isUpper('A')) acc += 2;\n" +
            "    if (std.ascii.isLower('z')) acc += 4;\n" +
            "    if (std.ascii.toUpper('a') == 'A') acc += 8;\n" +
            "    if (std.ascii.toLower('Q') == 'q') acc += 16;\n" +
            "    if (std.ascii.isWhitespace(' ')) acc += 32;\n" +
            "    return acc;\n" +
            "}\n";

        var workDir = Path.Combine(Path.GetTempPath(), $"dotcc-zig-stdascii-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var mainPath = Path.Combine(workDir, "main.zig");
        File.WriteAllText(mainPath, program);
        try
        {
            // dotcc: compile std.ascii from REAL upstream source (navigating @import("std") → ascii.zig),
            // lazily — only the referenced classifiers lower, not ascii's std-heavy tails.
            var emitted = Compiler.EmitCSharp(new[] { mainPath }, emit: EmitMode.Csproj);
            var (dotccStdout, dotccExit) = FixtureRunner.CompileAndRunCapturingExit(emitted, Array.Empty<string>());

            // zig: build + run the same root; its std is the same pinned source dotcc navigated.
            var (zigStdout, zigExit) = ZigOracle.CompileAndRun(mainPath, workDir);

            dotccExit.ShouldBe(zigExit, "dotcc's std.ascii-from-source diverges from real zig (exit code)");
            dotccExit.ShouldBe(63, "std.ascii classifiers did not produce the expected result");
            Norm(dotccStdout).ShouldBe(Norm(zigStdout), "dotcc's std.ascii-from-source diverges from real zig (stdout)");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The W4 lift against REAL upstream std: <c>std.meta.Child</c> (a <c>return switch</c> over
    /// <c>@typeInfo</c>), <c>std.meta.Elem</c> (a STATEMENT switch with returning prongs, a nested switch on
    /// a pointer's size class, and a trailing <c>@compileError</c>), and <c>std.math.Log2Int</c> /
    /// <c>Log2IntCeil</c> (comptime value consts, <c>@clz</c> at a <c>u16</c> width, <c>@Int</c>) — each
    /// compiled from source, then diffed against real zig. Needs <c>DOTCC_ZIG_LIB_DIR</c>, like the
    /// std.ascii differential; the four have the same shape in the pinned 0.17-dev std dotcc tracks.
    /// 30 + 4 + 2 + 5 + 1, and both widths are 6 bits (so the last two terms add 0) = 42.</summary>
    [Fact]
    public void Dotcc_matches_zig_std_type_functions_from_source()
    {
        if (!ZigRunRequested)
        {
            Assert.Skip($"Zig oracle is opt-in. Set {RunZigEnv}=1 to run the std type-function differential.");
        }
        if (!ZigOracle.IsAvailable)
        {
            Assert.Skip($"{RunZigEnv} requested but no `zig` is on PATH on this host.");
        }
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTCC_ZIG_LIB_DIR")))
        {
            Assert.Skip("DOTCC_ZIG_LIB_DIR must point at the zig lib dir so dotcc navigates the real std.meta / std.math source.");
        }

        const string program =
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    const a: std.meta.Child(*u8) = 30;\n" +
            "    const b: std.meta.Elem([]const u16) = 4;\n" +
            "    const c: std.meta.Elem([3]u8) = 2;\n" +
            "    const d: std.math.Log2Int(u64) = 5;\n" +
            "    const e: std.math.Log2IntCeil(u32) = 1;\n" +
            "    var total: u32 = a;\n" +
            "    total += b;\n" +
            "    total += c;\n" +
            "    total += d;\n" +
            "    total += e;\n" +
            "    total += @bitSizeOf(std.math.Log2Int(u64)) - 6;\n" +
            "    total += @bitSizeOf(std.math.Log2IntCeil(u32)) - 6;\n" +
            "    return @intCast(total);\n" +
            "}\n";

        var workDir = Path.Combine(Path.GetTempPath(), $"dotcc-zig-stdtypefns-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var mainPath = Path.Combine(workDir, "main.zig");
        File.WriteAllText(mainPath, program);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { mainPath }, emit: EmitMode.Csproj);
            var (dotccStdout, dotccExit) = FixtureRunner.CompileAndRunCapturingExit(emitted, Array.Empty<string>());
            var (zigStdout, zigExit) = ZigOracle.CompileAndRun(mainPath, workDir);

            dotccExit.ShouldBe(zigExit, "dotcc's std type functions from source diverge from real zig (exit code)");
            dotccExit.ShouldBe(42, "std.meta / std.math type functions did not produce the expected result");
            Norm(dotccStdout).ShouldBe(Norm(zigStdout), "dotcc's std type functions from source diverge from real zig (stdout)");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary><c>std.fmt.parseInt</c> compiled from REAL upstream std and diffed against real zig
    /// (road-to-zig-std G3): its comptime function alias (<c>const add = switch (sign) { .pos => math.add, … }</c>),
    /// <c>std.math.cast</c>'s <c>maxInt(@TypeOf(x))</c> over an <c>anytype</c> argument (the declared width a value
    /// carries), <c>comptime assert</c>, and a folded <c>else if</c> arm. Signed and unsigned, with a sign,
    /// an underscore separator and a base prefix; an overflow is an error. 40 + 7 - 5 + 0 = 42. Needs
    /// <c>DOTCC_ZIG_LIB_DIR</c>.</summary>
    [Fact]
    public void Dotcc_matches_zig_std_fmt_parse_int_from_source()
    {
        if (!ZigRunRequested)
        {
            Assert.Skip($"Zig oracle is opt-in. Set {RunZigEnv}=1 to run the std.fmt.parseInt differential.");
        }
        if (!ZigOracle.IsAvailable)
        {
            Assert.Skip($"{RunZigEnv} requested but no `zig` is on PATH on this host.");
        }
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTCC_ZIG_LIB_DIR")))
        {
            Assert.Skip("DOTCC_ZIG_LIB_DIR must point at the zig lib dir so dotcc navigates the real std.fmt source.");
        }

        const string program =
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    const a = std.fmt.parseInt(u8, \"4_0\", 10) catch return 1;\n" +
            "    const b = std.fmt.parseInt(u8, \"0x7\", 0) catch return 2;\n" +
            "    const c = std.fmt.parseInt(i8, \"-5\", 10) catch return 3;\n" +
            "    const over = std.fmt.parseInt(u8, \"300\", 10) catch 0;\n" +
            "    return @intCast(@as(i16, a) + b + c + over);\n" +
            "}\n";

        var workDir = Path.Combine(Path.GetTempPath(), $"dotcc-zig-stdparseint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var mainPath = Path.Combine(workDir, "main.zig");
        File.WriteAllText(mainPath, program);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { mainPath }, emit: EmitMode.Csproj);
            var (dotccStdout, dotccExit) = FixtureRunner.CompileAndRunCapturingExit(emitted, Array.Empty<string>());
            var (zigStdout, zigExit) = ZigOracle.CompileAndRun(mainPath, workDir);

            dotccExit.ShouldBe(zigExit, "dotcc's std.fmt.parseInt from source diverges from real zig (exit code)");
            dotccExit.ShouldBe(42, "std.fmt.parseInt did not produce the expected result");
            Norm(dotccStdout).ShouldBe(Norm(zigStdout), "dotcc's std.fmt.parseInt from source diverges from real zig (stdout)");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The typed <c>builtin.cpu</c> (the target-identity segment, T3): a real <c>std.Target.Cpu</c> read off
    /// the host through .NET's intrinsics. A comptime <c>std.atomic.cacheLineForCpu(builtin.cpu)</c> and a comptime
    /// <c>builtin.cpu.has(…)</c> run std.Target's own code in the interpreter; the features asked about are ones
    /// every host of the architecture has (SSE2 on x86_64, NEON on aarch64), so zig's native detection and .NET's
    /// agree.</summary>
    [Fact]
    public void Dotcc_matches_zig_std_builtin_cpu_features_from_source() =>
        MatchesZigWithRealStd("builtincpu",
            "const std = @import(\"std\");\n" +
            "const builtin = @import(\"builtin\");\n" +
            "pub fn main() u8 {\n" +
            "    const cl = comptime std.atomic.cacheLineForCpu(builtin.cpu);\n" +
            "    const simd = comptime (builtin.cpu.has(.x86, .sse2) or builtin.cpu.has(.aarch64, .neon));\n" +
            "    return @intCast(cl / 4 + @as(u16, @intFromBool(simd)) * 10);\n" +
            "}\n", 42);

    /// <summary><c>std.array_list.Aligned(u8, null)</c> growing through <c>page_allocator</c>, all of it from the real
    /// std source: the list's growth policy reads <c>std.atomic.cache_line</c>, a <c>comptime_int</c> computed by
    /// <c>cacheLineForCpu(builtin.cpu)</c> over the typed host cpu (T3).</summary>
    [Fact]
    public void Dotcc_matches_zig_std_array_list_from_source() =>
        MatchesZigWithRealStd("arraylist",
            "const std = @import(\"std\");\n" +
            "pub fn main() !u8 {\n" +
            "    const gpa = std.heap.page_allocator;\n" +
            "    var list: std.array_list.Aligned(u8, null) = .empty;\n" +
            "    defer list.deinit(gpa);\n" +
            "    try list.append(gpa, 40);\n" +
            "    try list.append(gpa, 2);\n" +
            "    return list.items[0] + list.items[1];\n" +
            "}\n", 42);

    /// <summary><c>std.simd.suggestVectorLength</c> (the target-identity segment, T4): the comptime question std's SIMD
    /// paths ask, answered by std.simd's own code over the typed host <c>builtin.cpu</c>, through a comptime STRUCT
    /// parameter (<c>comptime cpu: std.Target.Cpu</c>). The answer depends on the host's vector width, so the test
    /// requires only that dotcc (reading .NET's intrinsics) and zig (its native CPU detection) agree.</summary>
    [Fact]
    public void Dotcc_matches_zig_std_simd_suggest_vector_length_from_source() =>
        MatchesZigWithRealStd("suggestvl",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    const a = comptime std.simd.suggestVectorLength(u8) orelse 0;\n" +
            "    const b = comptime std.simd.suggestVectorLength(u32) orelse 0;\n" +
            "    return @intCast(a + b);\n" +
            "}\n", null);

    /// <summary>std.mem.indexOfScalar from source through its SIMD path (target T5): std.mem.findScalarPos asks
    /// std.simd.suggestVectorLength for a block length, loads `@Vector(block_len, u8)` blocks straight from the slice,
    /// compares them against a splatted needle and finds the first hit with std.simd.firstTrue (`@select` over
    /// std.simd.iota, `@reduce(.Min)`); its `{block_len, block_len / 2}` tail is an `inline for` whose
    /// `comptime if (block_x_len < 4) break;` stops the unroll, and std.simd.VectorIndex reaches
    /// std.math.IntFittingRange and log2 over a comptime_int. Hits in the unrolled loop, each tail block and the
    /// scalar remainder, plus misses.</summary>
    [Fact]
    public void Dotcc_matches_zig_std_mem_index_of_scalar_simd_from_source() =>
        MatchesZigWithRealStd("indexofscalar",
            "const std = @import(\"std\");\n" +
            "\n" +
            "fn at(n: usize, hit: usize) usize {\n" +
            "    var buf: [200]u8 = undefined;\n" +
            "    @memset(&buf, 'a');\n" +
            "    if (hit < n) buf[hit] = 'z';\n" +
            "    return std.mem.indexOfScalar(u8, buf[0..n], 'z') orelse 255;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var sum: usize = 0;\n" +
            "    // Hits in the unrolled 2x32 loop, the 32 and 16 blocks, the scalar tail, and misses.\n" +
            "    sum += at(200, 3);\n" +
            "    sum += at(200, 40);\n" +
            "    sum += at(200, 150);\n" +
            "    sum += at(90, 70);\n" +
            "    sum += at(50, 45);\n" +
            "    sum += at(20, 19);\n" +
            "    sum += at(5, 2);\n" +
            "    sum += at(100, 100);\n" +
            "    return @intCast(sum % 251);\n" +
            "}\n", 82);

    /// <summary>std.fmt.bufPrint from source (road-to-zig-std G3): the comptime format engine (std.Io.Writer.print's scan,
    /// std.fmt.Placeholder.parse run by the interpreter, the comptime argument bookkeeping) and the runtime integer
    /// printing (printValue, printInt, printIntAny, std.fmt.digits2) over a comptime_int, unsigned and signed widths and
    /// several arguments per format. The bytes are folded to one checksum, so a wrong digit shows.</summary>
    [Fact]
    public void Dotcc_matches_zig_std_fmt_buf_print_from_source() =>
        MatchesZigWithRealStd("bufprint",
            "const std = @import(\"std\");\n" +
            "\n" +
            "fn sum(s: []const u8) u32 {\n" +
            "    var t: u32 = 0;\n" +
            "    for (s, 0..) |c, i| t +%= @as(u32, c) *% @as(u32, @intCast(i + 1));\n" +
            "    return t;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var buf: [64]u8 = undefined;\n" +
            "    var total: u32 = 0;\n" +
            "    const a = std.fmt.bufPrint(&buf, \"{d}\", .{42}) catch return 1;\n" +
            "    total +%= sum(a);\n" +
            "    const b = std.fmt.bufPrint(&buf, \"x={d} y={d}\", .{ @as(u32, 1234), @as(i32, -56) }) catch return 2;\n" +
            "    total +%= sum(b);\n" +
            "    const c = std.fmt.bufPrint(&buf, \"{d}{d}{d}\", .{ @as(u8, 7), @as(u64, 1000000007), @as(i8, -128) }) catch return 3;\n" +
            "    total +%= sum(c);\n" +
            "    return @intCast(total % 251);\n" +
            "}\n", 159);

    /// <summary>std.mem.sort from source (road-to-zig-std, task #48): std.sort's block sort and insertion sort with
    /// their local <c>Context</c> structs, std.mem.swap / reverse, std.math.sqrt_int and log2 over a comptime_int, on 60
    /// values sorted ascending (std.sort.asc), descending (std.sort.desc) and by a custom context and comparator; the
    /// orders are folded to a checksum so a misplaced element shows.</summary>
    [Fact]
    public void Dotcc_matches_zig_std_mem_sort_from_source() =>
        MatchesZigWithRealStd("memsort",
            "const std = @import(\"std\");\n" +
            "\n" +
            "const ByMod = struct {\n" +
            "    m: u32,\n" +
            "    fn less(ctx: ByMod, a: u32, b: u32) bool {\n" +
            "        return (a % ctx.m) < (b % ctx.m) or ((a % ctx.m) == (b % ctx.m) and a < b);\n" +
            "    }\n" +
            "};\n" +
            "\n" +
            "fn check(xs: []const u32) u32 {\n" +
            "    var t: u32 = 0;\n" +
            "    for (xs, 0..) |x, i| t +%= x *% @as(u32, @intCast(i + 1));\n" +
            "    return t;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var a: [60]u32 = undefined;\n" +
            "    var seed: u32 = 12345;\n" +
            "    for (&a) |*x| {\n" +
            "        seed = seed *% 1103515245 +% 12345;\n" +
            "        x.* = (seed >> 8) % 1000;\n" +
            "    }\n" +
            "    var b = a;\n" +
            "    var c = a;\n" +
            "    std.mem.sort(u32, &a, {}, std.sort.asc(u32));\n" +
            "    std.mem.sort(u32, &b, {}, std.sort.desc(u32));\n" +
            "    std.mem.sort(u32, &c, ByMod{ .m = 7 }, ByMod.less);\n" +
            "    var ok: u8 = 0;\n" +
            "    for (1..a.len) |i| {\n" +
            "        if (a[i - 1] > a[i]) ok = 1;\n" +
            "        if (b[i - 1] < b[i]) ok = 2;\n" +
            "    }\n" +
            "    const sum = check(&a) +% check(&b) +% check(&c);\n" +
            "    return @intCast((sum % 200) + ok);\n" +
            "}\n", 137);

    /// <summary>std.AutoHashMap from source (road-to-zig-std, task #51): 200 inserts through several growths, overwrites,
    /// removals, hits and misses, `count()` and an iterator, folded to a checksum. Needed std.hash's Wyhash (slices at
    /// `*const [N]u8` parameters, `@bitCast` of byte arrays, a u128 multiply) and hash_map's packed `Metadata`, its
    /// header arithmetic and std.mem.swap of the whole map.</summary>
    [Fact]
    public void Dotcc_matches_zig_std_auto_hash_map_from_source() =>
        MatchesZigWithRealStd("autohashmap",
            "const std = @import(\"std\");\n" +
            "\n" +
            "pub fn main() !u8 {\n" +
            "    var m = std.AutoHashMap(u32, u32).init(std.heap.page_allocator);\n" +
            "    defer m.deinit();\n" +
            "    var i: u32 = 0;\n" +
            "    while (i < 200) : (i += 1) try m.put(i * 7, i);\n" +
            "    i = 0;\n" +
            "    while (i < 200) : (i += 3) try m.put(i * 7, i + 1000);\n" +
            "    i = 0;\n" +
            "    while (i < 200) : (i += 5) _ = m.remove(i * 7);\n" +
            "    var sum: u32 = 0;\n" +
            "    i = 0;\n" +
            "    while (i < 1400) : (i += 1) {\n" +
            "        if (m.get(i)) |v| sum +%= v *% (i + 1);\n" +
            "    }\n" +
            "    var it = m.iterator();\n" +
            "    var iter_sum: u32 = 0;\n" +
            "    while (it.next()) |e| iter_sum +%= e.key_ptr.* ^ e.value_ptr.*;\n" +
            "    const missing: u32 = if (m.get(3) == null) 1 else 0;\n" +
            "    return @intCast((sum +% iter_sum +% m.count() +% missing) % 251);\n" +
            "}\n", 131);

    /// <summary>A std string pipeline from source (road-to-zig-std, tasks #52 to #55): std.fmt.bufPrint with `{s}`, `{d}`
    /// and `{x}` over a tuple holding a string literal, ArrayList.appendSlice of those pieces and of `&amp;.{ 'e', 'n', 'd' }`,
    /// std.mem.splitScalar / tokenizeScalar, indexOf, eql, startsWith and endsWith, folded to a checksum. A string literal
    /// tuple element must not carry its NUL into the `{s}` slice.</summary>
    [Fact]
    public void Dotcc_matches_zig_std_string_pipeline_from_source() =>
        MatchesZigWithRealStd("strpipeline",
            "const std = @import(\"std\");\n" +
            "\n" +
            "fn check(s: []const u8) u32 {\n" +
            "    var t: u32 = 0;\n" +
            "    for (s, 0..) |c, i| t +%= @as(u32, c) *% @as(u32, @intCast(i + 1));\n" +
            "    return t;\n" +
            "}\n" +
            "\n" +
            "pub fn main() !u8 {\n" +
            "    const alloc = std.heap.page_allocator;\n" +
            "    var list: std.ArrayList(u8) = .empty;\n" +
            "    defer list.deinit(alloc);\n" +
            "    var i: u32 = 0;\n" +
            "    while (i < 40) : (i += 1) {\n" +
            "        var buf: [32]u8 = undefined;\n" +
            "        const piece = try std.fmt.bufPrint(&buf, \"{s}{d}:{x},\", .{ \"k\", i, i * 37 });\n" +
            "        try list.appendSlice(alloc, piece);\n" +
            "    }\n" +
            "    try list.appendSlice(alloc, &.{ 'e', 'n', 'd' });\n" +
            "    const text = list.items;\n" +
            "\n" +
            "    var total: u32 = 0;\n" +
            "    var it = std.mem.splitScalar(u8, text, ',');\n" +
            "    var parts: u32 = 0;\n" +
            "    while (it.next()) |p| {\n" +
            "        parts += 1;\n" +
            "        total +%= check(p);\n" +
            "    }\n" +
            "    var tk = std.mem.tokenizeScalar(u8, \"  alpha  beta gamma   \", ' ');\n" +
            "    while (tk.next()) |t| total +%= check(t) *% 3;\n" +
            "\n" +
            "    const pos = std.mem.indexOf(u8, text, \"k33:\") orelse 999;\n" +
            "    const long_a = text[0..40];\n" +
            "    const long_b = text[0..40];\n" +
            "    const eq: u32 = if (std.mem.eql(u8, long_a, long_b)) 1 else 0;\n" +
            "    const ne: u32 = if (std.mem.eql(u8, text[0..40], text[1..41])) 1 else 0;\n" +
            "    const sw: u32 = if (std.mem.startsWith(u8, text, \"k0:0,k1:25,\")) 1 else 0;\n" +
            "    const ew: u32 = if (std.mem.endsWith(u8, text, \",end\")) 1 else 0;\n" +
            "    return @intCast((total +% parts +% @as(u32, @intCast(pos)) +% eq *% 7 +% ne *% 11 +% sw *% 13 +% ew *% 17) % 251);\n" +
            "}\n", 55);

    /// <summary>std's small helpers from source (road-to-zig-std, tasks #57 and #58): trim, lastIndexOfScalar, count,
    /// replaceScalar, reverse, math.clamp (a three-operand @TypeOf), mem.min / max, mem.join (mem.zig's own Allocator is
    /// the curated one; dupe; `&amp;[0]u8{}`), StringHashMap, sort.insertion, ascii.eqlIgnoreCase, a padded bufPrint,
    /// readInt, splitSequence and tokenizeAny, folded to a checksum.</summary>
    [Fact]
    public void Dotcc_matches_zig_std_small_helpers_from_source() =>
        MatchesZigWithRealStd("stdhelpers",
            "const std = @import(\"std\");\n" +
            "\n" +
            "pub fn main() !u8 {\n" +
            "    const a = std.heap.page_allocator;\n" +
            "    var total: usize = 0;\n" +
            "\n" +
            "    total += std.mem.trim(u8, \"  ab c  \", \" \").len;\n" +
            "    total += std.mem.lastIndexOfScalar(u8, \"abcabc\", 'b').?;\n" +
            "    total += std.mem.count(u8, \"a,b,,c\", \",\");\n" +
            "\n" +
            "    var buf = [_]u8{ 1, 2, 1 };\n" +
            "    std.mem.replaceScalar(u8, &buf, 1, 5);\n" +
            "    std.mem.reverse(u8, &buf);\n" +
            "    total += buf[0] * 10 + buf[1];\n" +
            "\n" +
            "    total += std.math.clamp(@as(u8, 250), 3, 9);\n" +
            "    total += std.mem.min(u8, &[_]u8{ 4, 2, 9 }) + std.mem.max(u8, &[_]u8{ 4, 2, 9 });\n" +
            "\n" +
            "    const joined = try std.mem.join(a, \", \", &.{ \"a\", \"bc\", \"def\" });\n" +
            "    defer a.free(joined);\n" +
            "    total += joined.len;\n" +
            "\n" +
            "    var m = std.StringHashMap(u8).init(a);\n" +
            "    defer m.deinit();\n" +
            "    try m.put(\"x\", 4);\n" +
            "    try m.put(\"yy\", 5);\n" +
            "    total += m.get(\"yy\").? + m.count();\n" +
            "\n" +
            "    var s = [_]u8{ 3, 1, 2 };\n" +
            "    std.sort.insertion(u8, &s, {}, std.sort.asc(u8));\n" +
            "    total += s[0] + s[2];\n" +
            "\n" +
            "    total += @intFromBool(std.ascii.eqlIgnoreCase(\"HeLLo\", \"hello\"));\n" +
            "\n" +
            "    var out: [16]u8 = undefined;\n" +
            "    total += (try std.fmt.bufPrint(&out, \"{d:>5}|\", .{42})).len;\n" +
            "\n" +
            "    const b = [_]u8{ 1, 2 };\n" +
            "    total += std.mem.readInt(u16, &b, .little) % 200;\n" +
            "\n" +
            "    var it = std.mem.splitSequence(u8, \"a::b::c\", \"::\");\n" +
            "    while (it.next()) |_| total += 1;\n" +
            "    var tk = std.mem.tokenizeAny(u8, \" a, b ,c\", \" ,\");\n" +
            "    while (tk.next()) |p| total += p.len;\n" +
            "\n" +
            "    return @intCast(total % 256);\n" +
            "}\n", 230);

    /// <summary>std.bit_set from source (road-to-zig-std, task #61): StaticBitSet(16) (a `return packed struct(MaskInt)`)
    /// with set / toggle / setValue / unset / count / isSet / findFirstSet, and IntegerBitSet(8)'s `.full`
    /// (`~@as(MaskInt, 0)`).</summary>
    [Fact]
    public void Dotcc_matches_zig_std_bit_set_from_source() =>
        MatchesZigWithRealStd("bitset",
            "const std = @import(\"std\");\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var s: std.StaticBitSet(16) = .empty;\n" +
            "    s.set(3);\n" +
            "    s.set(9);\n" +
            "    s.set(12);\n" +
            "    s.toggle(3);\n" +
            "    s.setValue(5, true);\n" +
            "    s.unset(9);\n" +
            "    var total: usize = s.count() * 10;\n" +
            "    total += @intFromBool(s.isSet(12)) + @intFromBool(s.isSet(9));\n" +
            "    total += s.findFirstSet().?;\n" +
            "    var f: std.bit_set.IntegerBitSet(8) = .full;\n" +
            "    f.unset(0);\n" +
            "    total += f.count();\n" +
            "    return @intCast(total);\n" +
            "}\n", 33);

    /// <summary>std.fmt.parseFloat from source (road-to-zig-std, task #59): f64 and f32 over the fast path, Eisel-Lemire,
    /// the slow big-decimal path, hex floats, subnormals, max / overflow to inf, inf / nan, signs, underscores and an
    /// invalid input, every result folded into a bit-exact checksum (and printed, so stderr is compared too).</summary>
    [Fact]
    public void Dotcc_matches_zig_std_parse_float_from_source() =>
        MatchesZigWithRealStd("parsefloat",
            "const std = @import(\"std\");\n" +
            "\n" +
            "fn bits(s: []const u8) u64 {\n" +
            "    const f = std.fmt.parseFloat(f64, s) catch return 0xdead;\n" +
            "    return @bitCast(f);\n" +
            "}\n" +
            "\n" +
            "fn bits32(s: []const u8) u32 {\n" +
            "    const f = std.fmt.parseFloat(f32, s) catch return 0xbeef;\n" +
            "    return @bitCast(f);\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const inputs = [_][]const u8{\n" +
            "        \"2.5\",                       \"0\",                    \"-0\",                    \"1e10\",\n" +
            "        \"123456789012345678901234\",  \"3.141592653589793\",    \"1e-320\",                \"0x1.8p3\",\n" +
            "        \"1_000.5\",                   \"inf\",                  \"-inf\",                  \"nan\",\n" +
            "        \"2.2250738585072014e-308\",   \"9007199254740993\",     \"0.1\",                   \"abc\",\n" +
            "        \"1.7976931348623157e308\",    \"1e400\",                \"7.2057594037927933e16\", \"4.9e-324\",\n" +
            "    };\n" +
            "    var acc: u64 = 0;\n" +
            "    for (inputs, 0..) |s, i| {\n" +
            "        acc = std.math.rotl(u64, acc, 7) ^ (bits(s) +% i);\n" +
            "    }\n" +
            "    acc ^= bits32(\"1.5\") ^ bits32(\"3.4028235e38\") ^ bits32(\"1e-45\");\n" +
            "    std.debug.print(\"{x}\\n\", .{acc});\n" +
            "    return @truncate(acc ^ (acc >> 32) ^ (acc >> 16) ^ (acc >> 8));\n" +
            "}\n", 112);

    // Task #145: a FixedBufferAllocator runs out at zig's point (the first append of a u32 into 96 bytes).
    [Fact]
    public void Dotcc_matches_zig_std_array_list_fba_oom() =>
        MatchesZigWithRealStd("array_list_fba_oom",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    var buf: [96]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.init(&buf);\n" +
            "    const gpa = fba.allocator();\n" +
            "    var l: std.ArrayList(u32) = .empty;\n" +
            "    l.append(gpa, 1) catch return 1;\n" +
            "    const small = [_]u32{ 2, 3 };\n" +
            "    l.insertSlice(gpa, 0, &small) catch return 2;\n" +
            "    const big = [_]u32{ 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7 };\n" +
            "    l.insertSlice(gpa, 1, &big) catch |e| {\n" +
            "        if (e == error.OutOfMemory) return @intCast(40 + l.items.len + l.items[0]);\n" +
            "        return 3;\n" +
            "    };\n" +
            "    return 4;\n" +
            "}\n", 1);

    // Task #145: std.ArrayList capacities (append, appendSlice, insertSlice, ensureTotalCapacity, ensureUnusedCapacity) == zig.
    [Fact]
    public void Dotcc_matches_zig_std_array_list_capacity() =>
        MatchesZigWithRealStd("array_list_capacity",
            "const std = @import(\"std\");\n" +
            "pub fn main() !u8 {\n" +
            "    var buf: [8192]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.init(&buf);\n" +
            "    const gpa = fba.allocator();\n" +
            "    var a: std.ArrayList(u32) = .empty;\n" +
            "    try a.append(gpa, 1);\n" +
            "    const c1 = a.capacity;\n" +
            "    for (0..40) |i| try a.append(gpa, @intCast(i));\n" +
            "    const c2 = a.capacity;\n" +
            "    var b: std.ArrayList(u8) = .empty;\n" +
            "    try b.appendSlice(gpa, \"hello\");\n" +
            "    const c3 = b.capacity;\n" +
            "    try b.insertSlice(gpa, 2, \"abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz\");\n" +
            "    const c4 = b.capacity;\n" +
            "    var c: std.ArrayList(u64) = .empty;\n" +
            "    try c.ensureTotalCapacity(gpa, 3);\n" +
            "    const c5 = c.capacity;\n" +
            "    try c.ensureUnusedCapacity(gpa, 40);\n" +
            "    const c6 = c.capacity;\n" +
            "    std.debug.print(\"{d} {d} {d} {d} {d} {d}\\n\", .{ c1, c2, c3, c4, c5, c6 });\n" +
            "    return @intCast((c1 + c2 + c3 + c4 + c5 + c6) % 256);\n" +
            "}\n", 171);

    // Tasks #140, #148, #151..#156: std.crypto.hash.Blake3 from real std (its SIMD rounds over `@Vector(16, u32)`, the
    // `?[32]u8` key, `[][8]u32` / `[][*]const u8` slices, lane stores and the packed Flags): plain, keyed and incremental.
    [Fact]
    public void Dotcc_matches_zig_std_crypto_blake3() =>
        MatchesZigWithRealStd("crypto_blake3",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    var out: [32]u8 = undefined;\n" +
            "    std.crypto.hash.Blake3.hash(\"abc\", &out, .{});\n" +
            "    var keyed: [32]u8 = undefined;\n" +
            "    const key: [32]u8 = @splat(7);\n" +
            "    std.crypto.hash.Blake3.hash(\"abc\", &keyed, .{ .key = key });\n" +
            "    var h = std.crypto.hash.Blake3.init(.{});\n" +
            "    h.update(\"ab\");\n" +
            "    h.update(\"c\");\n" +
            "    var inc: [32]u8 = undefined;\n" +
            "    h.final(&inc);\n" +
            "    return out[0] ^ out[31] ^ keyed[5] ^ @as(u8, @intFromBool(std.mem.eql(u8, &out, &inc)));\n" +
            "}\n", 255);

    // Task #159: std.crypto.auth.siphash.SipHash64(2, 4).create from real std.
    [Fact]
    public void Dotcc_matches_zig_std_crypto_siphash() =>
        MatchesZigWithRealStd("crypto_siphash",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    const key: [16]u8 = @splat(1);\n" +
            "    var out: [8]u8 = undefined;\n" +
            "    std.crypto.auth.siphash.SipHash64(2, 4).create(&out, \"abc\", &key);\n" +
            "    return out[0] ^ out[7];\n" +
            "}\n", 149);

    // Task #160: std.crypto.hash.sha2.Sha512 from real std.
    [Fact]
    public void Dotcc_matches_zig_std_crypto_sha512() =>
        MatchesZigWithRealStd("crypto_sha512",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    var out: [64]u8 = undefined;\n" +
            "    std.crypto.hash.sha2.Sha512.hash(\"abc\", &out, .{});\n" +
            "    return out[0] ^ out[63];\n" +
            "}\n", 66);

    // Task #164: std.crypto.hash.sha3.Sha3_256 from real std (after #161 and #163).
    [Fact]
    public void Dotcc_matches_zig_std_crypto_sha3() =>
        MatchesZigWithRealStd("crypto_sha3",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    var out: [32]u8 = undefined;\n" +
            "    std.crypto.hash.sha3.Sha3_256.hash(\"abc\", &out, .{});\n" +
            "    return out[0] ^ out[31];\n" +
            "}\n", 8);

    // Task #162: std.mem.zeroInit from real std (field_attrs, `defaultValue`, zeroes of an array field).
    [Fact]
    public void Dotcc_matches_zig_std_mem_zero_init() =>
        MatchesZigWithRealStd("mem_zero_init",
            "const std = @import(\"std\");\n" +
            "const P = struct { a: u8, b: u32, c: [3]u8 };\n" +
            "pub fn main() u8 {\n" +
            "    var p = std.mem.zeroes(P);\n" +
            "    p.c[1] = 4;\n" +
            "    const q = std.mem.zeroInit(P, .{ .b = 7 });\n" +
            "    return p.a + @as(u8, @intCast(q.b)) + p.c[1] * 2;\n" +
            "}\n", 15);

    // Task #162: `field_attrs[i].defaultValue(T)` folded per field (0.17's `@typeInfo` shape, so real-std only).
    [Fact]
    public void Dotcc_matches_zig_field_attrs_default_value() =>
        MatchesZigWithRealStd("field_attrs_default_value",
            "const P = struct { a: u8, b: u32 = 5, c: [3]u8 };\n" +
            "fn f(comptime T: type) u32 {\n" +
            "    var n: u32 = 0;\n" +
            "    const info = @typeInfo(T).@\"struct\";\n" +
            "    inline for (info.field_names, info.field_types, info.field_attrs, 0..) |name, ft, attr, i| {\n" +
            "        _ = name;\n" +
            "        if (attr.@\"comptime\") continue;\n" +
            "        if (attr.defaultValue(ft)) |v| {\n" +
            "            n += v * 10;\n" +
            "        } else {\n" +
            "            n += i;\n" +
            "        }\n" +
            "    }\n" +
            "    return n;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return @intCast(f(P));\n" +
            "}\n", 52);

    // Task #168: std.crypto.auth.hmac.sha2.HmacSha256.create from real std.
    [Fact]
    public void Dotcc_matches_zig_std_crypto_hmac_sha256() =>
        MatchesZigWithRealStd("crypto_hmac_sha256",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    var out: [32]u8 = undefined;\n" +
            "    std.crypto.auth.hmac.sha2.HmacSha256.create(&out, \"msg\", \"key\");\n" +
            "    return out[0] ^ out[31];\n" +
            "}\n", 5);

    // Task #148: std.math.rotr / rotl from real std over a `@Vector(4, u32)` (Blake3's SIMD rounds rotate this way).
    [Fact]
    public void Dotcc_matches_zig_std_math_rotr_vector() =>
        MatchesZigWithRealStd("math_rotr_vector",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    const V = @Vector(4, u32);\n" +
            "    const v: V = .{ 1, 0x80000000, 3, 0xF0 };\n" +
            "    const r = std.math.rotr(V, v, 4);\n" +
            "    const l = std.math.rotl(V, v, 1);\n" +
            "    return @truncate(r[0] >> 24 ^ r[3] ^ l[1] ^ (l[2] << 2));\n" +
            "}\n", 6);

    // Task #147: std.mem.reverseIterator from real std over a pointer to an array (nextPtr) and a slice (next).
    [Fact]
    public void Dotcc_matches_zig_std_mem_reverse_iterator() =>
        MatchesZigWithRealStd("mem_reverse_iterator",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    var arr = [_]u8{ 1, 2, 3, 4 };\n" +
            "    var it = std.mem.reverseIterator(&arr);\n" +
            "    var acc: u8 = 0;\n" +
            "    while (it.nextPtr()) |p| {\n" +
            "        p.* += 1;\n" +
            "        acc = acc *% 2 +% p.*;\n" +
            "    }\n" +
            "    var it2 = std.mem.reverseIterator(@as([]const u8, \"xyz\"));\n" +
            "    const last = it2.next().?;\n" +
            "    return acc +% arr[0] +% (last - 'x');\n" +
            "}\n", 68);

    // Task #147: std.mem.ReverseIterator's shape: a switched @typeInfo payload, a `ptr.size` switch and `@Pointer`.
    // Local-only: `@Pointer` and `Type.Pointer.attrs` are 0.17-dev spellings CI's zig 0.16.0 may not have.
    [Fact]
    public void Dotcc_matches_zig_reverse_iterator_shape() =>
        MatchesZigWithRealStd("reverse_iterator_shape",
            "fn Rev(comptime T: type) type {\n" +
            "    const ptr = switch (@typeInfo(T)) {\n" +
            "        .pointer => |p| p,\n" +
            "        else => @compileError(\"expected a pointer\"),\n" +
            "    };\n" +
            "    switch (ptr.size) {\n" +
            "        .slice => {},\n" +
            "        .one => if (@typeInfo(ptr.child) != .array) @compileError(\"expected an array\"),\n" +
            "        .many, .c => @compileError(\"bad size\"),\n" +
            "    }\n" +
            "    const Element = ptr.child;\n" +
            "    const Pointer = @Pointer(.many, ptr.attrs, Element, null);\n" +
            "    return struct {\n" +
            "        ptr: Pointer,\n" +
            "        index: usize,\n" +
            "        pub fn next(self: *@This()) ?Element {\n" +
            "            if (self.index == 0) return null;\n" +
            "            self.index -= 1;\n" +
            "            return self.ptr[self.index];\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "fn rev(slice: anytype) Rev(@TypeOf(slice)) {\n" +
            "    return .{ .ptr = slice.ptr, .index = slice.len };\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const s: []const u8 = \"abc\";\n" +
            "    var it = rev(s);\n" +
            "    var n: u8 = 0;\n" +
            "    while (it.next()) |c| n = n * 3 + (c - 'a');\n" +
            "    return n;\n" +
            "}\n", 21);

    // Task #142: std.meta.activeTag and std.meta.Tag from real std.
    [Fact]
    public void Dotcc_matches_zig_std_meta_active_tag() =>
        MatchesZigWithRealStd("meta_active_tag",
            "const std = @import(\"std\");\n" +
            "const U = union(enum) { a: u8, b: u16, c };\n" +
            "pub fn main() u8 {\n" +
            "    const x: U = .{ .b = 5 };\n" +
            "    const y: U = .c;\n" +
            "    const tx = std.meta.activeTag(x);\n" +
            "    const T = std.meta.Tag(U);\n" +
            "    const z: T = .a;\n" +
            "    return @as(u8, @intFromEnum(tx)) * 10 + @intFromEnum(std.meta.activeTag(y)) + @intFromEnum(z) * 3;\n" +
            "}\n", 12);

    // Task #144: std.unicode.utf8ToUtf16Le from real std, long enough to take the vectorized ASCII path.
    [Fact]
    public void Dotcc_matches_zig_std_utf8_to_utf16le() =>
        MatchesZigWithRealStd("utf8_to_utf16le",
            "const std = @import(\"std\");\n" +
            "pub fn main() !u8 {\n" +
            "    var out: [40]u16 = undefined;\n" +
            "    const n = try std.unicode.utf8ToUtf16Le(&out, \"ASCII run long enough for a vector h\\u{e9}!\");\n" +
            "    var sum: u32 = 0;\n" +
            "    for (out[0..n]) |u| sum +%= u;\n" +
            "    return @intCast(n + sum % 100);\n" +
            "}\n", 95);

    // Task #143: std.math.sign from real std over i32, i8, u16 and f64.
    [Fact]
    public void Dotcc_matches_zig_std_math_sign() =>
        MatchesZigWithRealStd("math_sign",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    const a = std.math.sign(@as(i32, -7));\n" +
            "    const b = std.math.sign(@as(i8, 5));\n" +
            "    const c = std.math.sign(@as(u16, 0));\n" +
            "    const d = std.math.sign(@as(f64, -2.5));\n" +
            "    return @intCast(@as(i32, a) + 5 + @as(i32, b) * 10 + @as(i32, c) * 100 + @as(i32, d) * 3 + 20);\n" +
            "}\n", 31);

    // Task #116: a comptime block building a table from `@typeInfo(T).@"enum".field_names` (std.meta.stringToEnum's shape).
    // Local-only: CI's pinned zig 0.16.0 has no `field_names` on builtin.Type.Enum, so it cannot be a CI oracle program.
    [Fact]
    public void Dotcc_matches_zig_comptime_block_field_names_table() =>
        MatchesZigWithRealStd("comptime_block_field_names_table",
            "const Op = enum(u8) { add = 3, sub = 5, mul = 7 };\n" +
            "\n" +
            "fn Lookup(comptime T: type) type {\n" +
            "    return struct {\n" +
            "        names: [*]const []const u8,\n" +
            "        values: [*]const T,\n" +
            "        len: usize,\n" +
            "\n" +
            "        const Self = @This();\n" +
            "\n" +
            "        inline fn init(comptime pairs: anytype) Self {\n" +
            "            comptime {\n" +
            "                var names: [pairs.len][]const u8 = undefined;\n" +
            "                var values: [pairs.len]T = undefined;\n" +
            "                fill(pairs, &names, &values);\n" +
            "                const fin_names = names;\n" +
            "                const fin_values = values;\n" +
            "                return .{ .names = &fin_names, .values = &fin_values, .len = pairs.len };\n" +
            "            }\n" +
            "        }\n" +
            "\n" +
            "        fn fill(pairs: anytype, names: [][]const u8, values: []T) void {\n" +
            "            for (pairs, 0..) |kv, i| {\n" +
            "                names[i] = kv.@\"0\";\n" +
            "                values[i] = kv.@\"1\";\n" +
            "            }\n" +
            "        }\n" +
            "\n" +
            "        fn get(self: Self, str: []const u8) ?T {\n" +
            "            var i: usize = 0;\n" +
            "            while (i < self.len) : (i += 1) {\n" +
            "                const n = self.names[i];\n" +
            "                if (n.len != str.len) continue;\n" +
            "                var j: usize = 0;\n" +
            "                while (j < n.len and n[j] == str[j]) : (j += 1) {}\n" +
            "                if (j == n.len) return self.values[i];\n" +
            "            }\n" +
            "            return null;\n" +
            "        }\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "fn toEnum(comptime T: type, str: []const u8) ?T {\n" +
            "    const kvs = comptime build_kvs: {\n" +
            "        const KV = struct { []const u8, T };\n" +
            "        var kvs_array: [@typeInfo(T).@\"enum\".field_names.len]KV = undefined;\n" +
            "        for (@typeInfo(T).@\"enum\".field_names, 0..) |name, i| {\n" +
            "            kvs_array[i] = .{ name, @field(T, name) };\n" +
            "        }\n" +
            "        break :build_kvs kvs_array[0..];\n" +
            "    };\n" +
            "    const map = Lookup(T).init(kvs);\n" +
            "    return map.get(str);\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const a = toEnum(Op, \"sub\") orelse return 99;\n" +
            "    const b = toEnum(Op, \"mul\") orelse return 98;\n" +
            "    const c = toEnum(Op, \"div\");\n" +
            "    return @intFromEnum(a) * 10 + @intFromEnum(b) + @as(u8, if (c == null) 100 else 0);\n" +
            "}\n", 157);

    // Task #138: curated std.ArrayList insertSlice at the front, the middle and the end.
    [Fact]
    public void Dotcc_matches_zig_std_array_list_insert_slice() =>
        MatchesZigWithRealStd("array_list_insert_slice",
            "const std = @import(\"std\");\n" +
            "pub fn main() !u8 {\n" +
            "    var buf: [256]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.init(&buf);\n" +
            "    const gpa = fba.allocator();\n" +
            "    var l: std.ArrayList(u16) = .empty;\n" +
            "    defer l.deinit(gpa);\n" +
            "    try l.appendSlice(gpa, &.{ 1, 5 });\n" +
            "    try l.insertSlice(gpa, 1, &.{ 2, 3, 4 });\n" +
            "    try l.insertSlice(gpa, 0, &.{0});\n" +
            "    try l.insertSlice(gpa, l.items.len, &.{ 6, 7 });\n" +
            "    const more = [_]u16{ 8, 9 };\n" +
            "    try l.insertSlice(gpa, 8, &more);\n" +
            "    var acc: u16 = 0;\n" +
            "    for (l.items) |v| acc = acc * 3 +% v;\n" +
            "    return @truncate(acc +% @as(u16, @intCast(l.items.len)));\n" +
            "}\n", 175);

    // Task #139: std.hash.Fnv1a_64 from real std.
    [Fact]
    public void Dotcc_matches_zig_std_fnv1a_64() =>
        MatchesZigWithRealStd("fnv1a_64",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    var h = std.hash.Fnv1a_64.init();\n" +
            "    h.update(\"hel\");\n" +
            "    h.update(\"lo\");\n" +
            "    return @truncate(h.final() ^ std.hash.Fnv1a_64.hash(\"hello\") ^ std.hash.Fnv1a_32.hash(\"abc\"));\n" +
            "}\n", 11);

    // Task #137: a call as the slice operand of bytesAsSlice / sliceAsBytes runs once.
    [Fact]
    public void Dotcc_matches_zig_std_byte_views_evaluate_once() =>
        MatchesZigWithRealStd("byte_views_once",
            "const std = @import(\"std\");\n" +
            "var calls: u8 = 0;\n" +
            "var store = [_]u8{ 5, 0, 6, 0 };\n" +
            "var halves = [_]u16{ 0x0102, 0x0304 };\n" +
            "fn bytes() []u8 {\n" +
            "    calls += 1;\n" +
            "    return store[0..];\n" +
            "}\n" +
            "fn words() []u16 {\n" +
            "    calls += 1;\n" +
            "    return halves[0..];\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const w = std.mem.bytesAsSlice(u16, bytes());\n" +
            "    const b = std.mem.sliceAsBytes(words());\n" +
            "    return @intCast(w.len * 10 + w[1] + b.len + b[3] + calls * 20);\n" +
            "}\n", 73);

    // Task #137: std.mem.bytesAsSlice over a pointer to a byte array and over a byte slice, written through.
    [Fact]
    public void Dotcc_matches_zig_std_bytes_as_slice() =>
        MatchesZigWithRealStd("bytes_as_slice",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    var raw = [_]u8{ 1, 0, 2, 0, 3, 0, 4, 0 };\n" +
            "    const words = std.mem.bytesAsSlice(u16, &raw);\n" +
            "    words[1] = 7;\n" +
            "    const view = std.mem.bytesAsSlice(u32, raw[0..]);\n" +
            "    const b = [_]u8{ 1, 0, 2, 0, 3, 0 };\n" +
            "    const s = std.mem.bytesAsSlice(u16, &b);\n" +
            "    return @intCast(s.len * 10 + s[2] + raw[2] + view.len + (view[0] >> 16));\n" +
            "}\n", 49);

    // Task #135: std.StringArrayHashMapUnmanaged from real std (a stored u32 hash).
    [Fact]
    public void Dotcc_matches_zig_std_string_array_hash_map() =>
        MatchesZigWithRealStd("string_array_hash_map",
            "const std = @import(\"std\");\n" +
            "\n" +
            "pub fn main() !u8 {\n" +
            "    var buf: [65536]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.init(&buf);\n" +
            "    const gpa = fba.allocator();\n" +
            "    var s: std.StringArrayHashMapUnmanaged(u32) = .empty;\n" +
            "    defer s.deinit(gpa);\n" +
            "    try s.put(gpa, \"alpha\", 1);\n" +
            "    try s.put(gpa, \"beta\", 2);\n" +
            "    try s.put(gpa, \"alpha\", 5);\n" +
            "    _ = s.orderedRemove(\"beta\");\n" +
            "    return @intCast((s.get(\"alpha\") orelse 0) + s.count() * 100 + @as(u32, @intFromBool(s.contains(\"beta\"))) * 10);\n" +
            "}\n", 105);

    // Task #135: std.AutoArrayHashMapUnmanaged from real std past the linear-scan limit (put, swapRemove, orderedRemove,
    // getOrPut, keys/values, get, contains).
    [Fact]
    public void Dotcc_matches_zig_std_auto_array_hash_map() =>
        MatchesZigWithRealStd("auto_array_hash_map",
            "const std = @import(\"std\");\n" +
            "\n" +
            "pub fn main() !u8 {\n" +
            "    var buf: [65536]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.init(&buf);\n" +
            "    const gpa = fba.allocator();\n" +
            "    var m: std.AutoArrayHashMapUnmanaged(u32, u32) = .empty;\n" +
            "    defer m.deinit(gpa);\n" +
            "    var i: u32 = 0;\n" +
            "    while (i < 40) : (i += 1) try m.put(gpa, i * 7, i);\n" +
            "    _ = m.swapRemove(14);\n" +
            "    _ = m.orderedRemove(21);\n" +
            "    const gop = try m.getOrPut(gpa, 1000);\n" +
            "    if (!gop.found_existing) gop.value_ptr.* = 77;\n" +
            "    const again = try m.getOrPut(gpa, 7);\n" +
            "    var h: u32 = @intFromBool(again.found_existing);\n" +
            "    for (m.keys(), m.values()) |k, v| h = h *% 31 +% k +% v;\n" +
            "    h +%= (m.get(35) orelse 0) + @as(u32, @intCast(m.count()));\n" +
            "    h +%= @as(u32, @intFromBool(m.contains(14))) * 1000;\n" +
            "    return @truncate(h ^ (h >> 8) ^ (h >> 16));\n" +
            "}\n", 9);

    // Task #108: std.MultiArrayList from real std (append, set, swapRemove, orderedRemove, items, pop).
    [Fact]
    public void Dotcc_matches_zig_std_multi_array_list() =>
        MatchesZigWithRealStd("multi_array_list",
            "const std = @import(\"std\");\n" +
            "\n" +
            "const P = struct { a: u8, b: u32, c: u16 };\n" +
            "\n" +
            "pub fn main() !u8 {\n" +
            "    var buf: [4096]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.init(&buf);\n" +
            "    const gpa = fba.allocator();\n" +
            "    var list: std.MultiArrayList(P) = .empty;\n" +
            "    defer list.deinit(gpa);\n" +
            "    var i: u8 = 0;\n" +
            "    while (i < 6) : (i += 1) {\n" +
            "        try list.append(gpa, .{ .a = i, .b = @as(u32, i) * 100, .c = @as(u16, i) + 7 });\n" +
            "    }\n" +
            "    list.set(2, .{ .a = 40, .b = 1, .c = 2 });\n" +
            "    list.swapRemove(0);\n" +
            "    list.orderedRemove(1);\n" +
            "    const s = list.slice();\n" +
            "    const as = s.items(.a);\n" +
            "    const cs = s.items(.c);\n" +
            "    var h: u32 = 0;\n" +
            "    for (as, cs) |a, c| h = h *% 31 +% a +% c;\n" +
            "    const last = list.pop().?;\n" +
            "    h = h *% 31 +% last.b;\n" +
            "    return @truncate(h ^ (h >> 8) ^ @as(u32, @intCast(list.len)));\n" +
            "}\n", 141);

    // Task #128: std.fmt.comptimePrint over ints, bools and strings, widths, fills, alignments, bases, positional and named
    // arguments; the exit code hashes every formatted byte.
    [Fact]
    public void Dotcc_matches_zig_std_fmt_comptime_print() =>
        MatchesZigWithRealStd("fmt_comptime_print",
            "const std = @import(\"std\");\n" +
            "\n" +
            "const digest_len = 384;\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const lines = [_][]const u8{\n" +
            "        std.fmt.comptimePrint(\"[{d:5}]\", .{@as(i32, 5)}),\n" +
            "        std.fmt.comptimePrint(\"[{d:5}]\", .{@as(i32, -5)}),\n" +
            "        std.fmt.comptimePrint(\"[{d:5}]\", .{5}),\n" +
            "        std.fmt.comptimePrint(\"[{:5}]\", .{@as(u8, 5)}),\n" +
            "        std.fmt.comptimePrint(\"[{s:5}]\", .{\"ab\"}),\n" +
            "        std.fmt.comptimePrint(\"[{s:<5}]\", .{\"ab\"}),\n" +
            "        std.fmt.comptimePrint(\"[{s:^5}]\", .{\"ab\"}),\n" +
            "        std.fmt.comptimePrint(\"[{d:0>4}]\", .{7}),\n" +
            "        std.fmt.comptimePrint(\"[{d:*^7}]\", .{-12}),\n" +
            "        std.fmt.comptimePrint(\"[{x}|{X}|{b}|{o}]\", .{ 255, 255, 5, 8 }),\n" +
            "        std.fmt.comptimePrint(\"[{x}]\", .{@as(i8, -1)}),\n" +
            "        std.fmt.comptimePrint(\"[{c}{c}]\", .{ 'h', 105 }),\n" +
            "        std.fmt.comptimePrint(\"[{}|{any}]\", .{ true, false }),\n" +
            "        std.fmt.comptimePrint(\"[{1s}{0s}{1s}]\", .{ \"a\", \"b\" }),\n" +
            "        std.fmt.comptimePrint(\"[{u}]\", .{@as(u21, 0xe9)}),\n" +
            "        std.fmt.comptimePrint(\"{{x}}\", .{}),\n" +
            "        std.fmt.comptimePrint(\"[{}]\", .{-3}),\n" +
            "        std.fmt.comptimePrint(\"[{d}]\", .{0xFFFF_FFFF_FFFF_FFFF_FF}),\n" +
            "        std.fmt.comptimePrint(\"[{x:0>4}]\", .{@as(u8, 10)}),\n" +
            "        std.fmt.comptimePrint(\"[{s:5}]\", .{\"\\xc3\\xa9\"}),\n" +
            "        std.fmt.comptimePrint(\"SHA-512/{d}\", .{digest_len}),\n" +
            "        std.fmt.comptimePrint(\"tab\\there\\n\", .{}),\n" +
            "        std.fmt.comptimePrint(\"[{[a]d:.2}|{[b]s:>3}]\", .{ .a = 5, .b = \"q\" }),\n" +
            "    };\n" +
            "    // A rolling hash over every formatted byte, so any divergence changes the exit code.\n" +
            "    var h: u32 = 0;\n" +
            "    for (lines) |l| {\n" +
            "        std.debug.print(\"{s}\\n\", .{l});\n" +
            "        for (l) |c| h = h *% 31 +% c;\n" +
            "        h = h *% 31 +% 10;\n" +
            "    }\n" +
            "    return @truncate(h ^ (h >> 8) ^ (h >> 16) ^ (h >> 24));\n" +
            "}\n", 149);

    // Task #130: std.DynamicBitSet from real std (initEmpty, set, toggle, unset, resize both ways, findFirstSet, count).
    [Fact]
    public void Dotcc_matches_zig_std_dynamic_bit_set() =>
        MatchesZigWithRealStd("dynamic_bit_set",
            "const std = @import(\"std\");\n" +
            "\n" +
            "pub fn main() !u8 {\n" +
            "    var buf: [1024]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.init(&buf);\n" +
            "    const a = fba.allocator();\n" +
            "    var s = try std.DynamicBitSet.initEmpty(a, 70);\n" +
            "    defer s.deinit();\n" +
            "    const none: usize = if (s.findFirstSet()) |_| 1 else 0;\n" +
            "    s.set(65);\n" +
            "    s.set(3);\n" +
            "    s.toggle(4);\n" +
            "    s.unset(3);\n" +
            "    try s.resize(130, true);\n" +
            "    const first = s.findFirstSet() orelse 999;\n" +
            "    const count = s.count();\n" +
            "    try s.resize(10, false);\n" +
            "    const small = s.count();\n" +
            "    return @intCast(none + first + count + small);\n" +
            "}\n", 67);

    // Task #129: std.fmt.count from real std (std.Io.Writer.Discarding's `@alignCast(@fieldParentPtr("writer", w))`).
    [Fact]
    public void Dotcc_matches_zig_std_fmt_count() =>
        MatchesZigWithRealStd("fmt_count",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    return @intCast(std.fmt.count(\"{d}-{s}\", .{ 12345, \"ab\" }) * 10 + std.fmt.count(\"{x}\", .{@as(u16, 4095)}));\n" +
            "}\n", 83);

    // Task #131: std.enums.tagName from real std (`return inline for (field_names, field_values) … else null;`).
    [Fact]
    public void Dotcc_matches_zig_std_enums_tag_name() =>
        MatchesZigWithRealStd("enums_tag_name",
            "const std = @import(\"std\");\n" +
            "const E = enum { alpha, be }; pub fn main() u8 { return @intCast(@tagName(E.alpha).len * 10 + std.enums.tagName(E, .be).?.len); }\n", 52);

    // Task #127: std.mem.sliceTo over a mutable array pointer, a const slice and a sentinel many-item pointer.
    [Fact]
    public void Dotcc_matches_zig_std_slice_to() =>
        MatchesZigWithRealStd("slice_to",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    var buf = [_]u8{ 7, 8, 0, 9 };\n" +
            "    const head = std.mem.sliceTo(&buf, 0);\n" +
            "    head[0] = 1;\n" +
            "    const sl: []const u8 = &[_]u8{ 3, 4, 5 };\n" +
            "    const tail = std.mem.sliceTo(sl, 5);\n" +
            "    const z: [*:0]const u8 = \"hey\";\n" +
            "    const m = std.mem.sliceTo(z, 0);\n" +
            "    return @intCast(head.len * 100 + buf[0] * 10 + tail.len + m.len * 3);\n" +
            "}\n", 221);

    // Task #132: real std `{d}` of a range-for capture, of `i * i` and of `x + 1` (printIntAny asks each width).
    [Fact]
    public void Dotcc_matches_zig_std_fmt_computed_ints() =>
        MatchesZigWithRealStd("fmt_computed_ints",
            "const std = @import(\"std\");\n" +
            "pub fn main() !u8 {\n" +
            "    var buf: [32]u8 = undefined;\n" +
            "    var n: usize = 0;\n" +
            "    for (0..12) |i| n += (try std.fmt.bufPrint(&buf, \"{d}:{d}\", .{ i, i * i })).len;\n" +
            "    const x: u32 = 99999;\n" +
            "    n += (try std.fmt.bufPrint(&buf, \"{d}\", .{x + 1})).len;\n" +
            "    return @intCast(n);\n" +
            "}\n", 54);

    // Task #63: a static call on std.ArrayList(T) other than the removed managed init goes to real std's
    // array_list.Aligned(T, null) (growCapacity, as std.Io.Writer.Allocating calls it).
    [Fact]
    public void Dotcc_matches_zig_std_array_list_grow_capacity() =>
        MatchesZigWithRealStd("array_list_grow_capacity",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    return @intCast(std.array_list.Aligned(u8, null).growCapacity(10) + std.ArrayList(u32).growCapacity(4));\n" +
            "}\n", 181);

    // Tasks #60 / #63: std.fmt.allocPrint from real std: std.Io.Writer.Allocating growing past its initial capacity, with
    // {s} / {d} / {x}. Allocating's vtable names sendFile, compiled as a runtime trap (its parameter points to std.Io.File,
    // which dotcc cannot lower; GitHub issue #126).
    [Fact]
    public void Dotcc_matches_zig_std_alloc_print() =>
        MatchesZigWithRealStd("alloc_print",
            "const std = @import(\"std\");\n" +
            "\n" +
            "pub fn main() !u8 {\n" +
            "    const a = std.heap.page_allocator;\n" +
            "    const s = try std.fmt.allocPrint(a, \"{s}={d}/{x}\", .{ \"key\", @as(u32, 1234567), @as(u8, 255) });\n" +
            "    defer a.free(s);\n" +
            "    const long = \"a-rather-long-name-to-force-the-writer-to-grow-past-its-initial-capacity\";\n" +
            "    const t = try std.fmt.allocPrint(a, \"{s} {s} {d}\", .{ long, long, @as(u64, 18446744073709551615) });\n" +
            "    defer a.free(t);\n" +
            "    return @intCast((s.len + t.len + s[0] + t[t.len - 1]) % 251);\n" +
            "}\n", 89);

    // Task #126: std.mem.bytesAsValue (a write through it) and bytesToValue from a pointer to an array, a string literal and a
    // slice.
    [Fact]
    public void Dotcc_matches_zig_std_bytes_as_value() =>
        MatchesZigWithRealStd("bytes_as_value",
            "const std = @import(\"std\");\n" +
            "\n" +
            "const P = extern struct { a: u16, b: u16 };\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var buf = [_]u8{ 1, 0, 2, 0 };\n" +
            "    const p = std.mem.bytesAsValue(P, &buf);\n" +
            "    p.b = 7;\n" +
            "    const lit = std.mem.bytesToValue(u32, \"\\x05\\x00\\x00\\x01\");\n" +
            "    const sl: []const u8 = buf[0..];\n" +
            "    const q = std.mem.bytesToValue(P, sl[0..4]);\n" +
            "    return @intCast(buf[2] * 10 + q.a + (lit >> 24) + (lit & 0xff));\n" +
            "}\n", 77);

    // Tasks #119 / #124: two generators in one program (std.Random.Pcg and Sfc64). Each std.Random.init instance's local
    // `gen.fill` casts to its OWN `Ptr` (the body alias is a seed of the in-function struct's deferred method).
    [Fact]
    public void Dotcc_matches_zig_std_random_two_generators() =>
        MatchesZigWithRealStd("random_two_generators",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    var prng = std.Random.Pcg.init(11);\n" +
            "    var sfc = std.Random.Sfc64.init(5);\n" +
            "    var buf: [4]u8 = undefined;\n" +
            "    prng.random().bytes(&buf);\n" +
            "    return buf[0] +% buf[3] +% sfc.random().int(u8);\n" +
            "}\n", 221);

    // Task #119: std.Random.DefaultPrng (Xoshiro256) from real std: intRangeAtMost, uintLessThan, boolean, shuffle and
    // float. std.Random.init asserts `@typeInfo(Ptr).pointer.size == .one` of the `*Xoshiro256` it is handed; the size
    // class rides the pointer's spelling.
    [Fact]
    public void Dotcc_matches_zig_std_random_api() =>
        MatchesZigWithRealStd("random_api",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    var prng = std.Random.DefaultPrng.init(7);\n" +
            "    const r = prng.random();\n" +
            "    var t: u32 = 0;\n" +
            "    for (0..10) |_| t += r.intRangeAtMost(u8, 1, 6);\n" +
            "    t += r.uintLessThan(u32, 100);\n" +
            "    t += @intFromBool(r.boolean());\n" +
            "    var a = [_]u8{ 1, 2, 3, 4, 5, 6 };\n" +
            "    r.shuffle(u8, &a);\n" +
            "    const f = r.float(f64);\n" +
            "    t += a[0] * 10 + a[5] + @as(u32, @intFromFloat(f * 10));\n" +
            "    return @truncate(t);\n" +
            "}\n", 56);

    // Task #97: std.math.rotl / rotr from real std for u8, u16 and u32 (rotl(u8, 0x81, 1) had silently returned 2).
    [Fact]
    public void Dotcc_matches_zig_std_math_rotate() =>
        MatchesZigWithRealStd("math_rotate",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    const a = std.math.rotl(u8, 0b1000_0001, 1);\n" +
            "    const b = std.math.rotr(u16, 0x0001, 4);\n" +
            "    const c = std.math.rotl(u32, 0x8000_0001, 3);\n" +
            "    return @intCast(a + (b >> 12) + (c & 0xff));\n" +
            "}\n", 16);

    // Tasks #121 / #122: `{any}` of a struct (with a `u21` field) and of an integer, from real std. std.Io.Writer.printValue's
    // format guards settle at compile time, the struct arm walks `field_names` with `@field(value, f_name)`, and
    // std.Io.Limit's `unlimited = math.maxInt(usize)` lowers while math.zig is still being prepared.
    [Fact]
    public void Dotcc_matches_zig_std_fmt_any() =>
        MatchesZigWithRealStd("fmt_any",
            "const std = @import(\"std\");\n" +
            "const Pt = struct { x: i32, y: u21 };\n" +
            "pub fn main() u8 {\n" +
            "    var buf: [96]u8 = undefined;\n" +
            "    const s = std.fmt.bufPrint(&buf, \"{any}|{any}\", .{ Pt{ .x = -1, .y = 70000 }, @as(u16, 513) }) catch return 99;\n" +
            "    var sum: u32 = 0;\n" +
            "    for (s, 0..) |c, i| sum +%= @as(u32, c) *% @as(u32, @intCast(i + 1));\n" +
            "    return @truncate(sum +% s.len);\n" +
            "}\n", 213);

    // Task #96 (with #85): std.fmt.bufPrint of floats from real std, `{d}` and `{e}` of f64 and `{d}` of f32, through
    // std.fmt.float's render / binaryToDecimal / formatScientific / formatDecimal and its [326][2]u64 power-of-5 tables.
    [Fact]
    public void Dotcc_matches_zig_std_fmt_float_formatting() =>
        MatchesZigWithRealStd("fmt_float_formatting",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    var buf: [64]u8 = undefined;\n" +
            "    const a = std.fmt.bufPrint(&buf, \"{d}\", .{@as(f64, 3.25)}) catch return 1;\n" +
            "    var total: usize = a.len * 10 + (a[0] - '0');\n" +
            "    const b = std.fmt.bufPrint(&buf, \"{e}\", .{@as(f64, 1234.5)}) catch return 2;\n" +
            "    total += b.len;\n" +
            "    const c = std.fmt.bufPrint(&buf, \"{d}\", .{@as(f32, 0.1)}) catch return 3;\n" +
            "    total += c.len * 3;\n" +
            "    return @intCast(total % 256);\n" +
            "}\n", 60);

    // Task #94: std.EnumMap from real std (init from a struct of optionals, put, remove, getPtr, get, contains,
    // iterator). EnumMap.init walks the keys with `if (@field(init_values, tag)) |*v|`, and its
    // `EnumFieldStruct(E, ?Value, @as(?Value, null))` passes a typed comptime null.
    [Fact]
    public void Dotcc_matches_zig_std_enums_enum_map() =>
        MatchesZigWithRealStd("enums_enum_map",
            "const std = @import(\"std\");\n" +
            "const E = enum { red, green, blue, cyan };\n" +
            "pub fn main() u8 {\n" +
            "    var m = std.EnumMap(E, u8).init(.{ .cyan = 9, .red = 1 });\n" +
            "    m.put(.green, 4);\n" +
            "    m.remove(.red);\n" +
            "    if (m.getPtr(.cyan)) |p| p.* += 20;\n" +
            "    var total: u32 = @intCast(m.count());\n" +
            "    total += (m.get(.cyan) orelse 0) + (m.get(.green) orelse 0) + (m.get(.red) orelse 50);\n" +
            "    if (!m.contains(.blue)) total += 100;\n" +
            "    var it = m.iterator();\n" +
            "    while (it.next()) |entry| total += @as(u32, @intFromEnum(entry.key)) * 3 + entry.value.*;\n" +
            "    return @intCast(total % 256);\n" +
            "}\n", 230);

    // Task #93: std.EnumSet (init from a struct of bools, insert, iterator, initMany, unionWith / intersectWith)
    // and std.EnumArray.initDefault from real std. They go through std.enums.EnumFieldStruct's `@Struct` with
    // default-value pointers, `@tagName` + `@field` over the indexer's comptime keys, and std.bit_set's
    // `iterator(self, comptime options: IteratorOptions) Iterator(options)`. The root's `.{ .blue = 100 }` fills
    // the omitted fields from defaults the enums module recorded.
    [Fact]
    public void Dotcc_matches_zig_std_enums_enum_set() =>
        MatchesZigWithRealStd("enums_enum_set",
            "const std = @import(\"std\");\n" +
            "const E = enum { red, green, blue, cyan, magenta };\n" +
            "pub fn main() u8 {\n" +
            "    var s = std.EnumSet(E).init(.{ .green = true, .cyan = true });\n" +
            "    s.insert(.red);\n" +
            "    var it = s.iterator();\n" +
            "    var weight: u32 = 0;\n" +
            "    while (it.next()) |k| weight += @intFromEnum(k) + 1;\n" +
            "    const t = std.EnumSet(E).initMany(&.{ .red, .magenta });\n" +
            "    const u = s.unionWith(t);\n" +
            "    const x = s.intersectWith(t);\n" +
            "    var a = std.EnumArray(E, u16).initDefault(7, .{ .blue = 100 });\n" +
            "    a.set(.red, 3);\n" +
            "    const mv: u32 = @intFromBool(s.contains(.cyan)) + @as(u32, @intFromBool(t.contains(.green))) * 2;\n" +
            "    const total: u32 = weight * 10 + @as(u32, @intCast(u.count())) * 3 + @as(u32, @intCast(x.count())) + a.get(.blue) + a.get(.red) + a.get(.cyan) + mv;\n" +
            "    return @intCast(total % 256);\n" +
            "}\n", 194);

    // Task #89: std.enums.EnumIndexer, dense (identity layout) and sparse (sorted by value in the type body, `min` bound as
    // a comptime local and read through an alias in another module).
    [Fact]
    public void Dotcc_matches_zig_std_enums_enum_indexer() =>
        MatchesZigWithRealStd("enums_enum_indexer",
            "const std = @import(\"std\");\n" +
            "\n" +
            "const Dense = enum { north, east, south, west };\n" +
            "const Sparse = enum(u8) { low = 9, high = 40, mid = 20 };\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const D = std.enums.EnumIndexer(Dense);\n" +
            "    const S = std.enums.EnumIndexer(Sparse);\n" +
            "    var total: usize = D.count * 100 + S.count * 10;\n" +
            "    total += D.indexOf(.south) + S.indexOf(.high) * 2 + S.indexOf(.mid);\n" +
            "    total += @intFromEnum(S.keyForIndex(0));\n" +
            "    return @intCast(total % 256);\n" +
            "}\n", 190);

    // Task #83: std.mem.sortUnstable is pdqsort; its heuristics take `std.math.log2_int` of a comptime_int length bound
    // (`maxInt(usize) + 1` must not wrap), and `@inComptime()` guards a swap.
    [Fact]
    public void Dotcc_matches_zig_std_mem_sort_unstable() =>
        MatchesZigWithRealStd("mem_sort_unstable",
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    var a = [_]u8{ 9, 3, 7, 1, 8, 2, 6, 4, 5, 0, 11, 13, 12, 10, 15, 14, 19, 17, 18, 16, 25, 21, 23, 22, 24, 20, 30, 26, 29, 28, 27 };\n" +
            "    std.mem.sortUnstable(u8, &a, {}, std.sort.asc(u8));\n" +
            "    return a[0] + a[30];\n" +
            "}\n", 30);

    // std.enums.values from real std (task #89): the enum's members as a comptime slice, walked at runtime.
    [Fact]
    public void Dotcc_matches_zig_std_enums_values() =>
        MatchesZigWithRealStd("std_enums_values",
            "const std = @import(\"std\");\n" +
            "\n" +
            "const Suit = enum { clubs, diamonds, hearts, spades };\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const vs = std.enums.values(Suit);\n" +
            "    var total: usize = vs.len * 10;\n" +
            "    for (vs, 0..) |v, i| total += @intFromEnum(v) * i;\n" +
            "    return @intCast(total);\n" +
            "}\n", 54);

    // std.crypto.hash.Md5 and sha2.Sha224 / Sha256 from real std (task #90): one-shot hashes of three inputs and a streamed
    // update/final, every digest byte printed. The SHA-NI and ARMv8 assembly paths fold away (dotcc's target reports no `sha`).
    [Fact]
    public void Dotcc_matches_zig_std_crypto_hash() =>
        MatchesZigWithRealStd("std_crypto_hash",
            "const std = @import(\"std\");\n" +
            "\n" +
            "fn show(label: []const u8, digest: []const u8) void {\n" +
            "    std.debug.print(\"{s}:\", .{label});\n" +
            "    for (digest) |b| std.debug.print(\" {x}\", .{b});\n" +
            "    std.debug.print(\"\\n\", .{});\n" +
            "}\n" +
            "\n" +
            "pub fn main() void {\n" +
            "    const inputs = [_][]const u8{ \"\", \"abc\", \"The quick brown fox jumps over the lazy dog, twice over and then some more text past one block\" };\n" +
            "    for (inputs) |input| {\n" +
            "        var md5: [16]u8 = undefined;\n" +
            "        std.crypto.hash.Md5.hash(input, &md5, .{});\n" +
            "        show(\"md5\", &md5);\n" +
            "        var s224: [28]u8 = undefined;\n" +
            "        std.crypto.hash.sha2.Sha224.hash(input, &s224, .{});\n" +
            "        show(\"sha224\", &s224);\n" +
            "        var s256: [32]u8 = undefined;\n" +
            "        std.crypto.hash.sha2.Sha256.hash(input, &s256, .{});\n" +
            "        show(\"sha256\", &s256);\n" +
            "    }\n" +
            "    var h = std.crypto.hash.sha2.Sha256.init(.{});\n" +
            "    h.update(\"ab\");\n" +
            "    h.update(\"c\");\n" +
            "    var streamed: [32]u8 = undefined;\n" +
            "    h.final(&streamed);\n" +
            "    show(\"streamed\", &streamed);\n" +
            "}\n", 0);

    // std.bit_set.ArrayBitSet / StaticBitSet from real std (task #88): set, setRangeValue, findFirstSet / findLastSet,
    // toggleFirstSet, the `full` constant (a labeled block over `@splat(~@as(MaskInt, 0))` and last_item_mask), eql.
    [Fact]
    public void Dotcc_matches_zig_std_bit_set() =>
        MatchesZigWithRealStd("std_bit_set",
            "const std = @import(\"std\");\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var a = std.bit_set.ArrayBitSet(u64, 150).empty;\n" +
            "    a.set(3);\n" +
            "    a.set(70);\n" +
            "    a.set(149);\n" +
            "    a.setRangeValue(.{ .start = 10, .end = 20 }, true);\n" +
            "    var total: usize = a.count();\n" +
            "    total += a.findFirstSet() orelse 99;\n" +
            "    total += a.findLastSet() orelse 99;\n" +
            "    const first = a.toggleFirstSet() orelse 99;\n" +
            "    total += first;\n" +
            "    total += @intFromBool(a.isSet(3));\n" +
            "    const full = std.bit_set.ArrayBitSet(u64, 150).full;\n" +
            "    total += full.count();\n" +
            "    total += @intFromBool(a.eql(a));\n" +
            "    total += @intFromBool(a.eql(full));\n" +
            "    var small = std.StaticBitSet(12).empty;\n" +
            "    small.set(11);\n" +
            "    total += small.count() + (small.findFirstSet() orelse 0);\n" +
            "    return @truncate(total);\n" +
            "}\n", 75);

    // std.hash.XxHash32 / XxHash64 from real std (task #87): finalize's `inline 0, 1, 2, 3 => |count|` prongs over input
    // lengths 0 to 39 and a streaming update. A string literal's anytype length had silently been one too many.
    [Fact]
    public void Dotcc_matches_zig_std_hash_xxhash() =>
        MatchesZigWithRealStd("xxhash",
            "const std = @import(\"std\");\n" +
            "const xx = std.hash;\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const text = \"The quick brown fox jumps over the lazy dog, again and again.\";\n" +
            "    var acc: u64 = 0;\n" +
            "    var n: usize = 0;\n" +
            "    while (n <= 40) : (n += 3) {\n" +
            "        acc = acc *% 31 +% xx.XxHash32.hash(@intCast(n), text[0..n]);\n" +
            "        acc = acc *% 31 +% xx.XxHash64.hash(n, text[0..n]);\n" +
            "    }\n" +
            "    var h = xx.XxHash32.init(7);\n" +
            "    h.update(text[0..10]);\n" +
            "    h.update(text[10..33]);\n" +
            "    acc ^= h.final();\n" +
            "    std.debug.print(\"{x}\\n\", .{acc});\n" +
            "    return @truncate(acc ^ (acc >> 8) ^ (acc >> 16) ^ (acc >> 32));\n" +
            "}\n", 186);

    // std.math.gcd from real std (task #86): `switch (@TypeOf(a, b)) { comptime_int => …, else => |T| T }`, a compound shift
    // by an @intCast count, and a comptime_int result printed.
    [Fact]
    public void Dotcc_matches_zig_std_math_gcd() =>
        MatchesZigWithRealStd("math_gcd",
            "const std = @import(\"std\");\n" +
            "const math = std.math;\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var a: u32 = 84;\n" +
            "    var b: u64 = 1071;\n" +
            "    var c: u16 = 1;\n" +
            "    _ = .{ &a, &b, &c };\n" +
            "    const g1 = math.gcd(a, @as(u32, 36));\n" +
            "    const g2 = math.gcd(b, @as(u64, 462));\n" +
            "    const g3 = math.gcd(c, @as(u16, 9));\n" +
            "    const g4 = math.gcd(@as(u8, 0), @as(u8, 5));\n" +
            "    const g5 = math.gcd(48, 180);\n" +
            "    std.debug.print(\"{d} {d} {d} {d} {d}\\n\", .{ g1, g2, g3, g4, g5 });\n" +
            "    return @truncate(g1 + g2 + g3 + g4 + g5);\n" +
            "}\n", 51);

    // std.unicode.utf8ValidateSlice from real std (task #82): its first-byte table is `comptime first: { … a ++ b ++ c }`
    // over @splat arrays; ASCII, 2 / 3 / 4-byte, surrogate, overlong, truncated and invalid-start inputs.
    [Fact]
    public void Dotcc_matches_zig_std_unicode_validate() =>
        MatchesZigWithRealStd("unicode_validate",
            "const std = @import(\"std\");\n" +
            "const unicode = std.unicode;\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    var bits: u8 = 0;\n" +
            "    const cases = [_][]const u8{ \"plain\", \"caf\\u{e9}\", \"\\u{1F600} ok\", \"\\xed\\xa0\\x80\", \"\\xff\", \"\\xc0\\x80\", \"\\xe2\\x82\", \"a\\u{10FFFF}z\" };\n" +
            "    for (cases, 0..) |c, i| {\n" +
            "        if (unicode.utf8ValidateSlice(c)) bits |= @as(u8, 1) << @intCast(i);\n" +
            "    }\n" +
            "    std.debug.print(\"{d}\\n\", .{bits});\n" +
            "    return bits;\n" +
            "}\n", 135);

    // std.base64 from real std (tasks #75, #78): the standard and url_safe_no_pad codecs (a global Codecs whose
    // array fields a synthesized initializer fills, @splat tables, a lazily deferred fn-pointer alias), encode,
    // calcSizeForSlice, decode, and the InvalidCharacter error.
    [Fact]
    public void Dotcc_matches_zig_std_base64() =>
        MatchesZigWithRealStd("base64",
            "const std = @import(\"std\");\n" +
            "const b64 = std.base64;\n" +
            "\n" +
            "pub fn main() !u8 {\n" +
            "    var buf: [64]u8 = undefined;\n" +
            "    const e1 = b64.standard.Encoder.encode(&buf, \"hello, world!\");\n" +
            "    var sum: u32 = 0;\n" +
            "    for (e1) |c| sum = sum *% 31 +% c;\n" +
            "    var buf2: [64]u8 = undefined;\n" +
            "    const e2 = b64.url_safe_no_pad.Encoder.encode(&buf2, \"\\xfb\\xff\\xfe?\");\n" +
            "    for (e2) |c| sum = sum *% 31 +% c;\n" +
            "    var out: [64]u8 = undefined;\n" +
            "    const n = try b64.standard.Decoder.calcSizeForSlice(\"aGVsbG8sIHdvcmxkIQ==\");\n" +
            "    try b64.standard.Decoder.decode(out[0..n], \"aGVsbG8sIHdvcmxkIQ==\");\n" +
            "    for (out[0..n]) |c| sum = sum *% 31 +% c;\n" +
            "    var bad: u32 = 0;\n" +
            "    b64.standard.Decoder.decode(out[0..3], \"a$==\") catch |err| {\n" +
            "        bad = if (err == error.InvalidCharacter) 7 else 9;\n" +
            "    };\n" +
            "    sum +%= bad;\n" +
            "    std.debug.print(\"{s} {s} {s} {d} {d}\\n\", .{ e1, e2, out[0..n], n, sum });\n" +
            "    return @truncate(sum ^ (sum >> 8) ^ n);\n" +
            "}\n", 61);

    // std.mem.readVarInt from real std (task #76): big and little endian, signed and unsigned, 1 to 4 bytes into
    // u32 / i16 / i32 / u64 / i8, via `const signedness = @typeInfo(ReturnType).int.signedness;` and `@Int(signedness, …)`.
    [Fact]
    public void Dotcc_matches_zig_std_mem_read_var_int() =>
        MatchesZigWithRealStd("readvarint",
            "const std = @import(\"std\");\n" +
            "const mem = std.mem;\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const bytes = [_]u8{ 0xff, 0x02, 0x03, 0x84 };\n" +
            "    const a = mem.readVarInt(u32, &bytes, .big);\n" +
            "    const b = mem.readVarInt(u32, &bytes, .little);\n" +
            "    const c = mem.readVarInt(i16, bytes[0..2], .big);\n" +
            "    const d = mem.readVarInt(i32, bytes[1..4], .little);\n" +
            "    const e = mem.readVarInt(u64, bytes[0..3], .big);\n" +
            "    const f = mem.readVarInt(i8, bytes[0..1], .little);\n" +
            "    std.debug.print(\"{x} {x} {d} {d} {x} {d}\\n\", .{ a, b, c, d, e, f });\n" +
            "    const mix: u32 = a ^ b ^ @as(u32, @bitCast(@as(i32, c))) ^ @as(u32, @bitCast(d)) ^ @as(u32, @truncate(e)) ^ @as(u32, @bitCast(@as(i32, f)));\n" +
            "    return @truncate(mix ^ (mix >> 8) ^ (mix >> 16) ^ (mix >> 24));\n" +
            "}\n", 134);

    // std.unicode from real std (task #72): utf8CountCodepoints (the ASCII fast path, truncated / invalid / overlong
    // input), utf8Decode through `utf8Decode2(bytes[0..2].*)`, and utf8Encode. utf8ByteSequenceLength's
    // `else => error.Utf8InvalidStartByte` had silently returned the error code as a length.
    [Fact]
    public void Dotcc_matches_zig_std_unicode() =>
        MatchesZigWithRealStd("unicode",
            "const std = @import(\"std\");\n" +
            "const unicode = std.unicode;\n" +
            "\n" +
            "fn code(e: anyerror) u32 {\n" +
            "    return switch (e) {\n" +
            "        error.TruncatedInput => 900,\n" +
            "        error.Utf8InvalidStartByte => 901,\n" +
            "        else => 902,\n" +
            "    };\n" +
            "}\n" +
            "\n" +
            "fn count(s: []const u8) u32 {\n" +
            "    const n = unicode.utf8CountCodepoints(s) catch |e| return code(e);\n" +
            "    return @intCast(n);\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const a = count(\"h\\u{e9}llo \\u{1F600}\");\n" +
            "    const b = count(\"plain ascii text that is long enough for the fast path\");\n" +
            "    const c = count(\"\\xe2\\x82\");\n" +
            "    const d = count(\"\\xff\");\n" +
            "    const e = count(\"\\xc0\\x80\");\n" +
            "    const f = count(\"\\u{20AC}\\u{20AC}\\u{20AC}\");\n" +
            "    const cp = unicode.utf8Decode(\"\\u{20AC}\") catch 0;\n" +
            "    var buf: [4]u8 = undefined;\n" +
            "    const len = unicode.utf8Encode(0x1F600, &buf) catch 0;\n" +
            "    std.debug.print(\"{d} {d} {d} {d} {d} {d} {x} {d} {x}\\n\", .{ a, b, c, d, e, f, cp, len, buf[0] });\n" +
            "    return @truncate(a + b + c + d + e + f + cp + len + buf[3]);\n" +
            "}\n", 255);

    // std.hash.Crc32 and six more CRCs from real std, bit-exact (tasks #71 / #79): Crc(W, algorithm) takes a comptime
    // struct, its lookup_table is a labeled block the interpreter runs (element-pointer stores, @bitReverse, u32 shifts that
    // wrap), and std.hash's `Crc32 = crc.Crc32` resolves through crc.zig's same-module alias. Widths 5 to 64, reflected
    // and not, and an incremental update.
    [Fact]
    public void Dotcc_matches_zig_std_hash_crc() =>
        MatchesZigWithRealStd("crc",
            "const std = @import(\"std\");\n" +
            "const crc = std.hash.crc;\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const a = std.hash.Crc32.hash(\"The quick brown fox jumps over the lazy dog\");\n" +
            "    var inc = std.hash.Crc32.init();\n" +
            "    inc.update(\"The quick brown fox \");\n" +
            "    inc.update(\"jumps over the lazy dog\");\n" +
            "    const b = inc.final();\n" +
            "    const c = crc.Crc32Bzip2.hash(\"123456789\");\n" +
            "    const d = crc.Crc16Arc.hash(\"123456789\");\n" +
            "    const e = crc.Crc8Smbus.hash(\"123456789\");\n" +
            "    const f = crc.Crc64Xz.hash(\"123456789\");\n" +
            "    const g = crc.Crc5Usb.hash(\"123456789\");\n" +
            "    std.debug.print(\"{x} {x} {x} {x} {x} {x} {x}\\n\", .{ a, b, c, d, e, f, g });\n" +
            "    const mix = a ^ b ^ c ^ d ^ e ^ @as(u32, @truncate(f ^ (f >> 32))) ^ g;\n" +
            "    return @truncate(mix ^ (mix >> 8) ^ (mix >> 16) ^ (mix >> 24));\n" +
            "}\n", 172);

    // std.meta.eql from real std (task #70): `info.layout` folds to .auto / .@"extern" / .@"packed", a packed struct
    // compares as its backing bytes (a generated ==), and the struct prong recurses through `inline for` + @field.
    [Fact]
    public void Dotcc_matches_zig_std_meta_eql() =>
        MatchesZigWithRealStd("meta_eql",
            "const std = @import(\"std\");\n" +
            "\n" +
            "const Inner = struct { x: u16, y: i8 };\n" +
            "const Outer = struct { a: u32, in: Inner, tail: u8 };\n" +
            "const Flags = packed struct { lo: u4, hi: u4 };\n" +
            "const Ext = extern struct { p: u32, q: u32 };\n" +
            "\n" +
            "fn bit(ok: bool, n: u3) u8 {\n" +
            "    return @as(u8, @intFromBool(ok)) << n;\n" +
            "}\n" +
            "\n" +
            "pub fn main() u8 {\n" +
            "    const o1 = Outer{ .a = 1, .in = .{ .x = 2, .y = -3 }, .tail = 6 };\n" +
            "    var o2 = o1;\n" +
            "    var s: u8 = 0;\n" +
            "    s |= bit(std.meta.eql(o1, o2), 0);\n" +
            "    o2.in.y = 3;\n" +
            "    s |= bit(!std.meta.eql(o1, o2), 1);\n" +
            "    o2 = o1;\n" +
            "    o2.tail = 9;\n" +
            "    s |= bit(!std.meta.eql(o1, o2), 2);\n" +
            "    s |= bit(std.meta.eql(Flags{ .lo = 1, .hi = 2 }, Flags{ .lo = 1, .hi = 2 }), 3);\n" +
            "    s |= bit(!std.meta.eql(Flags{ .lo = 1, .hi = 2 }, Flags{ .lo = 2, .hi = 1 }), 4);\n" +
            "    s |= bit(std.meta.eql(Ext{ .p = 7, .q = 8 }, Ext{ .p = 7, .q = 8 }), 5);\n" +
            "    s |= bit(std.meta.eql([2]u32{ 1, 2 }, [2]u32{ 1, 2 }), 6);\n" +
            "    s |= bit(!std.meta.eql(@as(u64, 5), @as(u64, 6)), 7);\n" +
            "    return s;\n" +
            "}\n", 255);

    // std.math.pow / sqrt / divCeil and std.mem.lastIndexOf from real std (tasks #66 to #69): pow's integer prong
    // returns through a hoisted `catch unreachable`, so the `@compileError` after it is dead; powi's
    // `if (c) unreachable else 1` takes the sink's type; divCeil opens with @setRuntimeSafety; findLastLinear is a
    // `while (true) : (i -= 1)` loop.
    [Fact]
    public void Dotcc_matches_zig_std_math_pow_sqrt_divceil_lastindexof() =>
        MatchesZigWithRealStd("math_pow_lastindexof",
            "const std = @import(\"std\");\n" +
            "\n" +
            "pub fn main() !u8 {\n" +
            "    const p = std.math.pow(u32, 3, 4);\n" +
            "    const q = std.math.pow(u8, 2, 7);\n" +
            "    const r: u32 = @intFromFloat(std.math.sqrt(@as(f64, 144.0)));\n" +
            "    const c = try std.math.divCeil(u8, 17, 5);\n" +
            "    const last = std.mem.lastIndexOf(u8, \"abcabc\", \"bc\") orelse 99;\n" +
            "    const none = std.mem.lastIndexOf(u8, \"abcabc\", \"zz\") orelse 7;\n" +
            "    std.debug.print(\"{d} {d} {d} {d} {d} {d}\\n\", .{ p, q, r, c, last, none });\n" +
            "    return @intCast((p + q + r + c + last + none) % 256);\n" +
            "}\n", 236);

    /// <summary>Run <paramref name="program"/> through dotcc (navigating the real std at <c>DOTCC_ZIG_LIB_DIR</c>) and
    /// through zig, and require both to exit alike and print the same (and, when given, with
    /// <paramref name="expectedExit"/>).</summary>
    private static void MatchesZigWithRealStd(string tag, string program, int? expectedExit)
    {
        if (!ZigRunRequested)
        {
            Assert.Skip($"Zig oracle is opt-in. Set {RunZigEnv}=1 to run the real-std differential '{tag}'.");
        }
        if (!ZigOracle.IsAvailable)
        {
            Assert.Skip($"{RunZigEnv} requested but no `zig` is on PATH on this host.");
        }
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTCC_ZIG_LIB_DIR")))
        {
            Assert.Skip("DOTCC_ZIG_LIB_DIR must point at the zig lib dir so dotcc navigates the real std source.");
        }
        var workDir = Path.Combine(Path.GetTempPath(), $"dotcc-zig-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var mainPath = Path.Combine(workDir, "main.zig");
        File.WriteAllText(mainPath, program);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { mainPath }, emit: EmitMode.Csproj);
            var (dotccStdout, dotccExit) = FixtureRunner.CompileAndRunCapturingExit(emitted, Array.Empty<string>());
            var (zigStdout, zigExit) = ZigOracle.CompileAndRun(mainPath, workDir);

            dotccExit.ShouldBe(zigExit, $"dotcc diverges from real zig on '{tag}' (exit code)");
            if (expectedExit is { } expected) { dotccExit.ShouldBe(expected, $"'{tag}' did not produce the expected result"); }
            Norm(dotccStdout).ShouldBe(Norm(zigStdout), $"dotcc diverges from real zig on '{tag}' (stdout)");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary><c>builtin.cpu.arch.endian()</c> with a real std (the target-identity segment, T3a): the synthetic
    /// builtin spells the architecture as <c>std.Target.Cpu.Arch</c>, so the method is Target.zig's own, while
    /// <c>builtin.cpu.arch == .x86_64</c> still folds as a comptime question.</summary>
    [Fact]
    public void Dotcc_matches_zig_std_builtin_cpu_arch_from_source()
    {
        if (!ZigRunRequested)
        {
            Assert.Skip($"Zig oracle is opt-in. Set {RunZigEnv}=1 to run the builtin.cpu.arch differential.");
        }
        if (!ZigOracle.IsAvailable)
        {
            Assert.Skip($"{RunZigEnv} requested but no `zig` is on PATH on this host.");
        }
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTCC_ZIG_LIB_DIR")))
        {
            Assert.Skip("DOTCC_ZIG_LIB_DIR must point at the zig lib dir so dotcc navigates the real std.Target source.");
        }

        const string program =
            "const builtin = @import(\"builtin\");\n" +
            "pub fn main() u8 {\n" +
            "    const little: u8 = if (builtin.cpu.arch.endian() == .little) 40 else 0;\n" +
            "    const known: u8 = if (builtin.cpu.arch == .x86_64 or builtin.cpu.arch == .aarch64) 2 else 0;\n" +
            "    return little + known;\n" +
            "}\n";

        var workDir = Path.Combine(Path.GetTempPath(), $"dotcc-zig-builtinarch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var mainPath = Path.Combine(workDir, "main.zig");
        File.WriteAllText(mainPath, program);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { mainPath }, emit: EmitMode.Csproj);
            var (dotccStdout, dotccExit) = FixtureRunner.CompileAndRunCapturingExit(emitted, Array.Empty<string>());
            var (zigStdout, zigExit) = ZigOracle.CompileAndRun(mainPath, workDir);

            dotccExit.ShouldBe(zigExit, "dotcc's builtin.cpu.arch diverges from real zig (exit code)");
            dotccExit.ShouldBe(42, "builtin.cpu.arch.endian did not produce the expected result");
            Norm(dotccStdout).ShouldBe(Norm(zigStdout), "dotcc's builtin.cpu.arch diverges from real zig (stdout)");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary><c>std.Target.Cpu.Arch.endian()</c> from REAL upstream Target.zig (the target-identity segment,
    /// T1/T2): a type nested two containers deep in another module, whose enum body nests `Family`.</summary>
    [Fact]
    public void Dotcc_matches_zig_std_target_arch_from_source()
    {
        if (!ZigRunRequested)
        {
            Assert.Skip($"Zig oracle is opt-in. Set {RunZigEnv}=1 to run the std.Target differential.");
        }
        if (!ZigOracle.IsAvailable)
        {
            Assert.Skip($"{RunZigEnv} requested but no `zig` is on PATH on this host.");
        }
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTCC_ZIG_LIB_DIR")))
        {
            Assert.Skip("DOTCC_ZIG_LIB_DIR must point at the zig lib dir so dotcc navigates the real std.Target source.");
        }

        const string program =
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    const a: std.Target.Cpu.Arch = .x86_64;\n" +
            "    const b: std.Target.Cpu.Arch = .powerpc;\n" +
            "    const little: u8 = if (a.endian() == .little) 40 else 0;\n" +
            "    const big: u8 = if (b.endian() == .big) 2 else 0;\n" +
            "    return little + big;\n" +
            "}\n";

        var workDir = Path.Combine(Path.GetTempPath(), $"dotcc-zig-stdtarget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var mainPath = Path.Combine(workDir, "main.zig");
        File.WriteAllText(mainPath, program);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { mainPath }, emit: EmitMode.Csproj);
            var (dotccStdout, dotccExit) = FixtureRunner.CompileAndRunCapturingExit(emitted, Array.Empty<string>());
            var (zigStdout, zigExit) = ZigOracle.CompileAndRun(mainPath, workDir);

            dotccExit.ShouldBe(zigExit, "dotcc's std.Target.Cpu.Arch from source diverges from real zig (exit code)");
            dotccExit.ShouldBe(42, "std.Target.Cpu.Arch.endian did not produce the expected result");
            Norm(dotccStdout).ShouldBe(Norm(zigStdout), "dotcc's std.Target.Cpu.Arch from source diverges from real zig (stdout)");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary><c>std.math.order</c> from REAL upstream std (road-to-zig-std G4): its <c>Order</c> enum carries
    /// a <c>test invert { … }</c> block inside the enum body, which dotcc now parses and drops, and the call
    /// is an <c>anytype</c> generic instantiated in std.math.</summary>
    [Fact]
    public void Dotcc_matches_zig_std_math_order_from_source()
    {
        if (!ZigRunRequested)
        {
            Assert.Skip($"Zig oracle is opt-in. Set {RunZigEnv}=1 to run the std.math.order differential.");
        }
        if (!ZigOracle.IsAvailable)
        {
            Assert.Skip($"{RunZigEnv} requested but no `zig` is on PATH on this host.");
        }
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTCC_ZIG_LIB_DIR")))
        {
            Assert.Skip("DOTCC_ZIG_LIB_DIR must point at the zig lib dir so dotcc navigates the real std.math source.");
        }

        const string program =
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    const a: u32 = 1;\n" +
            "    const lt: u8 = switch (std.math.order(a, 2)) { .lt => 40, .eq => 0, .gt => 0 };\n" +
            "    const gt: u8 = if (std.math.order(a, 0) == .gt) 2 else 0;\n" +
            "    return lt + gt;\n" +
            "}\n";

        var workDir = Path.Combine(Path.GetTempPath(), $"dotcc-zig-stdorder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var mainPath = Path.Combine(workDir, "main.zig");
        File.WriteAllText(mainPath, program);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { mainPath }, emit: EmitMode.Csproj);
            var (dotccStdout, dotccExit) = FixtureRunner.CompileAndRunCapturingExit(emitted, Array.Empty<string>());
            var (zigStdout, zigExit) = ZigOracle.CompileAndRun(mainPath, workDir);

            dotccExit.ShouldBe(zigExit, "dotcc's std.math.order from source diverges from real zig (exit code)");
            dotccExit.ShouldBe(42, "std.math.order did not produce the expected result");
            Norm(dotccStdout).ShouldBe(Norm(zigStdout), "dotcc's std.math.order from source diverges from real zig (stdout)");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The comptime-call engine V1 against REAL upstream std: <c>std.math.maxInt</c> /
    /// <c>minInt</c> return <c>comptime_int</c>, so every call folds at compile time (including the 64-bit
    /// extremes, a declared <c>u21</c> width, and a use inside a runtime comparison). Needs
    /// <c>DOTCC_ZIG_LIB_DIR</c>, like the std type-function differential above. 10 * 4 + 2 = 42.</summary>
    [Fact]
    public void Dotcc_matches_zig_std_math_max_min_int_from_source()
    {
        if (!ZigRunRequested)
        {
            Assert.Skip($"Zig oracle is opt-in. Set {RunZigEnv}=1 to run the std.math maxInt/minInt differential.");
        }
        if (!ZigOracle.IsAvailable)
        {
            Assert.Skip($"{RunZigEnv} requested but no `zig` is on PATH on this host.");
        }
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTCC_ZIG_LIB_DIR")))
        {
            Assert.Skip("DOTCC_ZIG_LIB_DIR must point at the zig lib dir so dotcc navigates the real std.math source.");
        }

        const string program =
            "const std = @import(\"std\");\n" +
            "const math = std.math;\n" +
            "pub fn main() u8 {\n" +
            "    const a: u64 = math.maxInt(u64);\n" +
            "    const b: i64 = math.minInt(i64);\n" +
            "    const c: i8 = math.minInt(i8);\n" +
            "    const d: u21 = math.maxInt(u21);\n" +
            "    var x: u32 = 70000;\n" +
            "    _ = &x;\n" +
            "    var r: u8 = 0;\n" +
            "    if (x > math.maxInt(u16)) r += 10;\n" +
            "    if (a == 18446744073709551615) r += 10;\n" +
            "    if (b == -9223372036854775808) r += 10;\n" +
            "    if (c == -128 and d == 2097151) r += 10;\n" +
            "    if (math.minInt(u8) == 0) r += 2;\n" +
            "    return r;\n" +
            "}\n";

        var workDir = Path.Combine(Path.GetTempPath(), $"dotcc-zig-stdmaxint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var mainPath = Path.Combine(workDir, "main.zig");
        File.WriteAllText(mainPath, program);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { mainPath }, emit: EmitMode.Csproj);
            var (dotccStdout, dotccExit) = FixtureRunner.CompileAndRunCapturingExit(emitted, Array.Empty<string>());
            var (zigStdout, zigExit) = ZigOracle.CompileAndRun(mainPath, workDir);

            dotccExit.ShouldBe(zigExit, "dotcc's std.math maxInt/minInt from source diverge from real zig (exit code)");
            dotccExit.ShouldBe(42, "std.math maxInt/minInt did not produce the expected result");
            Norm(dotccStdout).ShouldBe(Norm(zigStdout), "dotcc's std.math maxInt/minInt from source diverge from real zig (stdout)");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The CURATED allocators must keep working while a real std source tree is configured.
    /// Upstream re-exports its allocators as whole FILES
    /// (<c>pub const FixedBufferAllocator = @import("heap/FixedBufferAllocator.zig");</c>), so with
    /// <c>DOTCC_ZIG_LIB_DIR</c> set the path is both a curated type and a navigable module — and
    /// navigation used to win, making real-std navigation and the curated allocators mutually exclusive
    /// (6 oracle programs failed with the std root set, silently, because the suite's default
    /// configuration doesn't set one). This is the leg that keeps the two configurations from drifting
    /// apart again against the SHAPES of real upstream source; the always-on structural pins run over a
    /// synthetic tree in <c>ZigCuratedStdVsNavigationTests</c>. Needs <c>DOTCC_ZIG_LIB_DIR</c>, like the
    /// std.ascii differential above.</summary>
    [Fact]
    public void Dotcc_matches_zig_for_curated_allocators_with_real_std_configured()
    {
        if (!ZigRunRequested)
        {
            Assert.Skip($"Zig oracle is opt-in. Set {RunZigEnv}=1 to run the curated-allocators-with-real-std differential.");
        }
        if (!ZigOracle.IsAvailable)
        {
            Assert.Skip($"{RunZigEnv} requested but no `zig` is on PATH on this host.");
        }
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTCC_ZIG_LIB_DIR")))
        {
            Assert.Skip("DOTCC_ZIG_LIB_DIR must point at the zig lib dir so the curated-vs-navigation collision is live.");
        }

        // Both curated allocator types, each reached through the dotted std path that also names a real
        // upstream file: the FBA over a stack buffer, and an arena over the page allocator.
        const string program =
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    var buf: [256]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.init(&buf);\n" +
            "    const fa = fba.allocator();\n" +
            "    const s = fa.alloc(u8, 4) catch return 1;\n" +
            "    s[0] = 10;\n" +
            "    s[1] = 30;\n" +
            "    const from_fba = s[0] + s[1];\n" +
            "    fa.free(s);\n" +
            "    var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);\n" +
            "    defer arena.deinit();\n" +
            "    const aa = arena.allocator();\n" +
            "    const t = aa.alloc(u8, 2) catch return 2;\n" +
            "    t[0] = 2;\n" +
            "    return from_fba + t[0];\n" +
            "}\n";

        var workDir = Path.Combine(Path.GetTempPath(), $"dotcc-zig-stdalloc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var mainPath = Path.Combine(workDir, "main.zig");
        File.WriteAllText(mainPath, program);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { mainPath }, emit: EmitMode.Csproj);
            var (dotccStdout, dotccExit) = FixtureRunner.CompileAndRunCapturingExit(emitted, Array.Empty<string>());
            var (zigStdout, zigExit) = ZigOracle.CompileAndRun(mainPath, workDir);

            dotccExit.ShouldBe(zigExit, "dotcc's curated allocators diverge from real zig with a std root configured (exit code)");
            dotccExit.ShouldBe(42, "the curated allocators did not produce the expected result");
            Norm(dotccStdout).ShouldBe(Norm(zigStdout));
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
    /// <remarks>Four rows: too few to shard (<see cref="TestShard"/>), so the sharded runner gives the whole method to one
    /// host.</remarks>
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
