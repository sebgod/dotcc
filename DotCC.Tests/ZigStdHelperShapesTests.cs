#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for the shapes std's small helpers needed (road-to-zig-std, tasks #57 to #59): <c>@TypeOf(a, b, c)</c> as the
/// operands' peer type (std.math.clamp), an empty array literal and <c>allocator.dupe</c> in a value <c>if</c> at a
/// <c>![]u8</c> return (std.mem.join), a local <c>const M = switch (T) { f16, f32, f64 => u64, … }</c> and
/// <c>T == f16 or T == f32</c> over zig floats dotcc does not lower, a value <c>if</c> / <c>switch</c> as a named
/// field's value, <c>&amp;.{ i.base + 'a' }</c> as a comptime string, <c>inline for</c> over a comptime string, and a
/// value <c>if (c) return x else y</c> (std.fmt.parse_float). End to end in the <c>std_helper_shapes</c> zig-oracle
/// program and the real-std helpers differential.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigStdHelperShapesTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zighelp-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private const string Program = """
        const std = @import("std");

        fn clampLike(val: anytype, lower: anytype, upper: anytype) @TypeOf(val, lower, upper) {
            return @max(lower, @min(val, upper));
        }

        fn mantissaSize(comptime T: type) usize {
            const M = switch (T) {
                f16, f32, f64 => u64,
                f80, f128 => u128,
                else => unreachable,
            };
            if (T == f16 or T == f32 or T == f64) {
                return @sizeOf(M);
            }
            return 1;
        }

        const Info = struct { base: u8, max: u8 };

        fn info(comptime T: type) Info {
            return .{ .base = switch (T) {
                u64 => 10,
                else => 16,
            }, .max = if (T == u64) 19 else 38 };
        }

        fn hasAny(s: []const u8, comptime cs: []const u8) bool {
            for (s) |c| {
                inline for (cs) |d| if (c == d) return true;
            }
            return false;
        }

        fn hasSep(s: []const u8, comptime i: Info) bool {
            return hasAny(s, &.{i.base + 'a'});
        }

        fn firstOrNull(s: []const u8) ?u8 {
            return if (s.len > 0) return s[0] else null;
        }

        fn dupeOrEmpty(a: std.mem.Allocator, z: bool) ![]u8 {
            return if (z) try a.dupe(u8, &[1]u8{7}) else &[0]u8{};
        }

        pub fn main() !u8 {
            const a = std.heap.page_allocator;
            const d = try dupeOrEmpty(a, true);
            defer a.free(d);
            const e = try dupeOrEmpty(a, false);
            const none = [_]u8{};
            const i = comptime info(u64);
            const c: u16 = clampLike(@as(u16, 300), 1, 250);
            var total: usize = c - 200;
            total += mantissaSize(f64) + i.base + i.max + d[0] + e.len + none.len;
            total += @intFromBool(hasSep("xk", i)) + @intFromBool(hasSep("xy", i));
            total += (firstOrNull("A") orelse 0) - 'A' + 1;
            return @intCast(total);
        }
        """;

    [Fact]
    public void TypeOf_over_several_operands_is_their_peer_type()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("internal static unsafe ushort clampLike__u16_ci1_ci250(ushort val)");
    }

    [Fact]
    public void A_type_switch_and_type_comparisons_pass_over_floats_dotcc_does_not_lower()
    {
        var cs = EmitZig(Program);
        cs.ShouldMatch(@"ulong mantissaSize__f64\(\)\s*\{\s*\{\s*return \(\(ulong\)\(sizeof\(ulong\)\)\);");
    }

    [Fact]
    public void A_named_field_value_may_be_a_value_switch_or_if()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("return new Info { @base = 10, max = 19 };");
    }

    [Fact]
    public void An_anonymous_list_of_comptime_bytes_is_a_comptime_string_an_inline_for_unrolls()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("byte d = 107;");   // `i.base + 'a'` = 10 + 97
    }

    [Fact]
    public void A_value_if_whose_then_arm_returns_hoists_the_return()
    {
        var cs = EmitZig(Program);
        cs.ShouldMatch(@"byte\? firstOrNull\(ConstSlice<byte> s\)\s*\{\s*if \(Cond\.B\(\(\(CBool\)\(s\.Len > \(ulong\)\(0\)\)\)\)\)\s*return s\.Ptr\[0\];\s*return null;");
    }

    [Fact]
    public void Dupe_and_an_empty_array_at_an_error_union_slice_return()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("byte* __cl0 = stackalloc byte[]{ 7 };");
        cs.ShouldContain("return ErrUnion<Slice<byte>>.Ok((Cond.B(z) ? ErrUnion.Try(ZigAlloc.Dupe(a, new ConstSlice<byte>(__cl0, 1UL), 1)) : new Slice<byte>(null, 0UL)));");
    }

    [Fact]
    public void An_empty_inferred_array_literal_is_a_zero_length_array()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("stackalloc byte[]{  };");
    }

    private const string CompoundProgram = """
        fn firstOrNull(s: []const u8) ?u8 {
            if (s.len == 0) return null;
            return s[0];
        }

        pub fn main() u8 {
            var total: u8 = 1;
            total += if (firstOrNull("")) |_| 100 else 2;
            total *= if (firstOrNull("B")) |_| 2 else 3;
            return total;
        }
        """;

    [Fact]
    public void A_compound_assignment_hoists_a_captured_value_if()
    {
        var cs = EmitZig(CompoundProgram);
        cs.ShouldContain("total += __ifcap1;");
        cs.ShouldContain("total *= __ifcap3;");
    }

    [Fact]
    public void A_compound_assignment_with_a_side_effecting_target_does_not_reorder_its_value()
    {
        Should.Throw<Exception>(() => EmitZig("""
            fn firstOrNull(s: []const u8) ?u8 {
                if (s.len == 0) return null;
                return s[0];
            }
            var calls: u8 = 0;
            fn bump() u8 {
                calls += 1;
                return calls;
            }
            pub fn main() u8 {
                var buf = [_]u8{ 0, 0 };
                buf[bump() - 1] += if (firstOrNull("x")) |c| c else 0;
                return buf[0];
            }
            """)).Message.ShouldContain("can't be hoisted past an earlier side-effecting operand");
    }

    private const string BiasedProgram = """
        fn assert(ok: bool) void {
            if (!ok) unreachable;
        }

        fn mantissaType(comptime T: type) type {
            return switch (T) {
                f16, f32, f64 => u64,
                f80, f128 => u128,
                else => unreachable,
            };
        }

        fn Biased(comptime T: type) type {
            const MantissaT = mantissaType(T);
            return struct {
                const Self = @This();
                f: MantissaT,
                e: i32,

                pub fn word(self: Self) MantissaT {
                    var w: MantissaT = self.f;
                    w |= @as(MantissaT, @intCast(self.e)) << 8;
                    return w;
                }

                pub fn scaled(self: Self, comptime k: u8) MantissaT {
                    return self.f * @as(MantissaT, k);
                }
            };
        }

        fn check(comptime T: type) bool {
            assert(T == f16 or T == f32 or T == f64);
            return @sizeOf(T) == 8;
        }

        pub fn main() u8 {
            const b = Biased(f64){ .f = 3, .e = 1 };
            const w = b.word();
            return @intCast(w % 256 + (w >> 8) + b.scaled(4) + @intFromBool(check(f64)));
        }
        """;

    [Fact]
    public void A_type_bodys_own_type_alias_reaches_its_methods_and_generic_method_instances()
    {
        var cs = EmitZig(BiasedProgram);
        cs.ShouldContain("w |= (ulong)(ulong)self.e << 8;");                  // `@as(MantissaT, …)` in a deferred method
        cs.ShouldMatch(@"ulong Biased__f64_scaled__4\(Biased__f64 self\)\s*\{\s*return self\.f \* \(ulong\)4;");   // a generic method
    }

    [Fact]
    public void A_type_comparison_folds_as_a_call_argument()
    {
        var cs = EmitZig(BiasedProgram);
        cs.ShouldContain("assert(((CBool)(Cond.B(((CBool)(Cond.B(false) || Cond.B(false)))) || Cond.B(true))));");
    }

    [Fact]
    public void A_switch_prong_body_may_be_a_compound_assignment()
    {
        var cs = EmitZig("""
            var hits: u32 = 0;
            pub fn main() u8 {
                var bits: u8 = 0b0001;
                for ([_]u8{ 0, 1, 2, 3, 1, 0 }) |x| {
                    switch (x) {
                        0 => hits += 1,
                        1 => hits *= 3,
                        2 => bits |= 0b1000,
                        else => hits -%= 1,
                    }
                }
                var v: u8 = 7;
                switch (bits) {
                    9 => v <<= 2,
                    else => v = 0,
                }
                return @intCast(hits + bits + v);
            }
            """);
        cs.ShouldContain("hits += (uint)(1);");
        cs.ShouldContain("hits *= (uint)(3);");
        cs.ShouldContain("bits |= (byte)(8);");
        cs.ShouldContain("v <<= 2;");
    }

    [Fact]
    public void Debug_print_s_of_a_byte_slice_passes_the_slice_itself()
    {
        var cs = EmitZig("""
            const std = @import("std");
            pub fn main() void {
                const word: []const u8 = "hello world";
                var buf = [_]u8{ 'a', 'b', 'c', 'd' };
                const mut: []u8 = buf[1..3];
                std.debug.print("[{s}] [{s}] [{s}]\n", .{ word[0..5], mut, word[6..] });
            }
            """);
        // The runtime builder's slice overload prints exactly `.len` bytes; no NUL is read.
        cs.ShouldContain(".Arg(new ConstSlice<byte>(word.Ptr + 0, unchecked((ulong)(5 - 0)))).Arg(mut)");
    }

    [Fact]
    public void Float_builtins_route_to_System_Math_with_zigs_rounding()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                @setRuntimeSafety(false);
                var x: f64 = 2.5;
                var y: f32 = 6.25;
                _ = .{ &x, &y };
                const r = @round(x) + @sqrt(x * 10.0) + @abs(-x);
                const q = @sqrt(y);
                const f: u8 = @intFromFloat(r + q);
                return f;
            }
            """);
        // @round rounds half away from zero (C# Math.Round would round 2.5 to even); f32 keeps MathF.
        cs.ShouldContain("double r = ZigMath.RoundAway(x) + System.Math.Sqrt(x * 10.0) + System.Math.Abs(-x);");
        cs.ShouldContain("float q = System.MathF.Sqrt(y);");
        cs.ShouldNotContain("setRuntimeSafety");
    }

    [Fact]
    public void A_folded_if_returning_through_a_hoisted_catch_makes_the_rest_dead()
    {
        var cs = EmitZig("""
            fn powi(comptime T: type, x: T, y: T) error{Overflow}!T {
                if (y > 30) return error.Overflow;
                var acc: T = if (@typeInfo(T).int.bits < 1) unreachable else 1;
                var i: T = 0;
                while (i < y) : (i += 1) acc *= x;
                return acc;
            }
            fn pow(comptime T: type, x: T, y: T) T {
                if (@typeInfo(T) == .int) {
                    return powi(T, x, y) catch unreachable;
                }
                if (T != f32 and T != f64) {
                    @compileError("pow not implemented");
                }
                return x;
            }
            pub fn main() u8 {
                return @intCast(pow(u32, 3, 4));
            }
            """);
        // zig never analyses the `@compileError` after the taken prong's return (task #66), and the
        // `unreachable` arm leaves the ternary at the sink's type so the literal arm is cast.
        cs.ShouldContain("? throw new System.Diagnostics.UnreachableException(\"unreachable() reached\") : (uint)(1))");
    }

    [Fact]
    public void A_struct_layout_folds_and_a_packed_struct_compares_by_its_bytes()
    {
        var cs = EmitZig("""
            const Flags = packed struct { lo: u4, hi: u4 };
            const Ext = extern struct { p: u32 };
            const Plain = struct { p: u32 };
            fn kind(comptime T: type) u8 {
                return switch (@typeInfo(T).@"struct".layout) {
                    .auto => 1,
                    .@"extern" => 2,
                    .@"packed" => 3,
                };
            }
            pub fn main() u8 {
                const a = Flags{ .lo = 1, .hi = 2 };
                const b = Flags{ .lo = 1, .hi = 2 };
                return kind(Plain) * 100 + kind(Ext) * 10 + kind(Flags) + @intFromBool(a == b);
            }
            """);
        // Each instance returns its folded prong (task #70); zig's packed `==` compares the backing storage.
        cs.ShouldContain("public static bool operator ==(Flags a, Flags b) => System.MemoryExtensions.SequenceEqual(");
        cs.ShouldNotContain("operator ==(Ext a");
        cs.ShouldContain("(a == b)");
    }

    [Fact]
    public void A_while_true_with_a_continue_expression_has_no_condition()
    {
        var cs = EmitZig("""
            fn lastZero(s: []const u8) ?usize {
                var i: usize = s.len;
                while (true) : (i -= 1) {
                    if (i == 0) return null;
                    if (s[i - 1] == 0) return i - 1;
                }
            }
            pub fn main() u8 {
                return @intCast(lastZero(&[_]u8{ 1, 0, 2 }) orelse 9);
            }
            """);
        // An empty condition lets C# see the loop never falls out (Cond.B(true) is CS0161 here).
        cs.ShouldContain("for (; ; i -= (ulong)(1))");
    }

    [Fact]
    public void A_const_computed_by_a_labeled_block_is_evaluated_at_compile_time()
    {
        var cs = EmitZig("""
            const table = blk: {
                var t: [4]u32 = undefined;
                for (&t, 0..) |*e, i| {
                    e.* = @as(u32, @intCast(i)) * 5;
                }
                break :blk t;
            };
            const S = struct {
                const inner = blk: {
                    var t: [4]u32 = undefined;
                    for (&t, 0..) |*e, i| {
                        e.* = @as(u32, @intCast(i)) + 1;
                    }
                    break :blk t;
                };
                fn get(i: usize) u32 {
                    return inner[i];
                }
            };
            pub fn main() u8 {
                var i: usize = 3;
                _ = &i;
                return @truncate(table[i] + S.get(i));
            }
            """);
        // zig runs the block at compile time (task #79): the static holds the values, and no loop is emitted for it.
        cs.ShouldContain("uint* table = Libc.GlobalArrayFrom<uint>(new uint[]{ 0u, 5u, 10u, 15u });");
        cs.ShouldContain("uint* S__inner__static = Libc.GlobalArrayFrom<uint>(new uint[]{ 1u, 2u, 3u, 4u });");
    }

    [Fact]
    public void A_type_returning_generic_takes_a_comptime_struct_value()
    {
        var cs = EmitZig("""
            fn Algo(comptime W: type) type {
                return struct { poly: W, initial: W, refl: bool };
            }
            fn Crc(comptime W: type, comptime a: Algo(W)) type {
                return struct {
                    const Self = @This();
                    const table = blk: {
                        var t: [4]W = undefined;
                        for (&t, 0..) |*e, i| {
                            e.* = @as(W, @intCast(i)) * a.poly;
                        }
                        break :blk t;
                    };
                    crc: W,
                    pub fn init() Self {
                        const v = if (a.refl) a.initial + 1 else a.initial;
                        return Self{ .crc = v };
                    }
                    pub fn hash(b: []const u8) W {
                        var c = init();
                        for (b) |x| c.crc = table[x & 3] ^ (c.crc >> 1);
                        return c.crc;
                    }
                };
            }
            const C = Crc(u32, .{ .poly = 5, .initial = 7, .refl = true });
            pub fn main() u8 {
                return @truncate(C.hash("hello"));
            }
            """);
        // The struct argument keys the instance by its contents (task #71), and the members read its fields.
        cs.ShouldContain("__table__static = Libc.GlobalArrayFrom<uint>(new uint[]{ 0u, 5u, 10u, 15u });");
        cs.ShouldContain("return (byte)Crc__u32_c");
        cs.ShouldContain("_hash(new ConstSlice<byte>(");
    }

    [Fact]
    public void Bit_reverse_reverses_the_declared_width()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                var x: u8 = 0b0000_0110;
                var y: u3 = 0b011;
                _ = .{ &x, &y };
                return @bitReverse(x) + @as(u8, @bitReverse(y));
            }
            """);
        // A `u3` in its byte carrier reverses three bits, not eight.
        cs.ShouldContain("ZigMath.BitReverse(x, 8)");
        cs.ShouldContain("ZigMath.BitReverse(y, 3)");
    }

    [Fact]
    public void A_comptime_length_slice_dereference_passes_to_an_array_parameter()
    {
        var cs = EmitZig("""
            fn two(b: [2]u8) u16 {
                return @as(u16, b[0]) * 256 + b[1];
            }
            fn pair(s: []const u8) u16 {
                return two(s[0..2].*) + two(s[1..3].*);
            }
            pub fn main() u8 {
                return @truncate(pair("\x01\x02\x03"));
            }
            """);
        // std.unicode.utf8Decode's `utf8Decode2(bytes[0..2].*)` (task #72): the array parameter is its element pointer.
        cs.ShouldContain("two(new ConstSlice<byte>(s.Ptr + 0, unchecked((ulong)(2 - 0))).Ptr)");
    }

    [Fact]
    public void A_returned_if_or_switch_with_an_error_arm_is_the_error_union_itself()
    {
        var cs = EmitZig("""
            fn pick(x: u8) !u8 {
                return if (x < 10) x * 2 else error.TooBig;
            }
            fn kind(x: u8) !u8 {
                return switch (x) {
                    0 => 7,
                    1...5 => x + 1,
                    else => error.Bad,
                };
            }
            pub fn main() u8 {
                const a = pick(30) catch 100;
                const b = kind(9) catch 50;
                return a + b;
            }
            """);
        // Each arm is Ok or Err (task #72); wrapping the whole expression in Ok had returned the error code as a payload.
        cs.ShouldContain("? ErrUnion<byte>.Ok((byte)(x * 2)) : ErrUnion<byte>.Err(");
        cs.ShouldContain("_ => ErrUnion<byte>.Err(");
        cs.ShouldNotContain("ErrUnion<byte>.Ok((byte)((Cond.B(");
    }

    [Fact]
    public void Debug_print_braces_of_a_bool_prints_true_or_false()
    {
        var cs = EmitZig("""
            const std = @import("std");
            pub fn main() void {
                var x: u8 = 3;
                _ = &x;
                const t = x > 2;
                std.debug.print("{} {any}\n", .{ t, x < 1 });
            }
            """);
        // A local bound to a comparison is a zig `bool` (task #81), and `{}` prints it as a word.
        cs.ShouldContain("CBool t = ");
        cs.ShouldContain(""".Arg((Cond.B(t) ? Libc.L("true\0"u8) : Libc.L("false\0"u8)))""");
    }

    [Fact]
    public void A_signedness_tag_bound_to_a_const_builds_an_int_type()
    {
        var cs = EmitZig("""
            fn widen(comptime T: type, v: T) u16 {
                const signedness = @typeInfo(T).int.signedness;
                const W = @Int(signedness, 16);
                const w: W = v;
                return @bitCast(w);
            }
            pub fn main() u8 {
                return @truncate(widen(i8, -3) +% widen(u8, 200));
            }
            """);
        // std.mem.readVarInt's `const signedness = @typeInfo(ReturnType).int.signedness;` (task #76): a comptime tag.
        cs.ShouldContain("short w = v;");
        cs.ShouldContain("ushort w = v;");
    }

    [Fact]
    public void A_type_chosen_by_a_comptime_if_compares_equal_to_its_arm()
    {
        var cs = EmitZig("""
            const small = [_]u8{ 1, 2, 3 };
            const big = [_]u8{ 7, 8, 9, 10 };
            fn pick(comptime T: type) u8 {
                const DT = if (@bitSizeOf(T) <= 64) u64 else u128;
                const k: u8 = switch (DT) {
                    u64 => 1,
                    u128 => 2,
                    else => 3,
                };
                const tables = switch (DT) {
                    u64 => &small,
                    u128 => &big,
                    else => unreachable,
                };
                return @sizeOf(DT) + k + tables[1] + @as(u8, @intCast(tables.len));
            }
            pub fn main() u8 {
                return pick(f64) + pick(u128);
            }
            """);
        // std.fmt.float.render's `DT` (task #77): `DT == u64` holds (it had silently taken `else`), `&small` of an array
        // global is its storage pointer, and `.len` reads through a pointer to an array.
        cs.ShouldContain("byte k = 1;");
        cs.ShouldContain("byte* tables = (byte*)small;");
        cs.ShouldContain("unchecked((byte)(byte)3UL)");
    }

    [Fact]
    public void Array_list_index_and_capacity_members_map_onto_the_runtime_list()
    {
        var cs = EmitZig("""
            const std = @import("std");
            pub fn main() !u8 {
                const a = std.heap.page_allocator;
                var l: std.ArrayList(u16) = .empty;
                defer l.deinit(a);
                try l.appendSlice(a, &.{ 10, 20, 30 });
                try l.insert(a, 0, 5);
                const o = l.orderedRemove(1);
                const s = l.swapRemove(0);
                try l.ensureUnusedCapacity(a, 1);
                l.appendAssumeCapacity(60);
                l.shrinkRetainingCapacity(2);
                const last = l.getLast() orelse 0;
                return @intCast(o + s + last);
            }
            """);
        // Task #74: each member is one runtime ZigList call; getLast is `?T` in zig 0.17-dev.
        cs.ShouldContain(".Insert(ZigAlloc.CHeap(), 0, 5, ");
        cs.ShouldContain(".OrderedRemove(1)");
        cs.ShouldContain(".SwapRemove(0)");
        cs.ShouldContain(".EnsureUnusedCapacity(ZigAlloc.CHeap(), 1, ");
        cs.ShouldContain(".AppendAssumeCapacity(60)");
        cs.ShouldContain(".ShrinkRetainingCapacity(2)");
        cs.ShouldContain(".GetLast() ?? 0");
    }

    [Fact]
    public void A_global_struct_with_an_array_field_is_built_by_an_init_function()
    {
        var cs = EmitZig("""
            const alphabet = "ABCD".*;
            const Codec = struct { chars: [4]u8, table: [8]u8, pad: u8 };
            const codec = Codec{ .chars = alphabet, .table = @splat(0xff), .pad = '=' };
            pub fn main() u8 {
                return codec.chars[@truncate(codec.pad & 3)] +% codec.table[7];
            }
            """);
        // Task #78: the array fields are copied in by a synthesized initializer (a C# object initializer cannot set them);
        // task #75: `"ABCD".*` is the array itself, `@splat` fills a `[N]T`, and an index takes a cast builtin's type.
        cs.ShouldContain("Codec codec = __init_codec();");
        cs.ShouldContain("memcpy(__anf0.chars, ");
        cs.ShouldContain("stackalloc byte[]{ 255, 255, 255, 255, 255, 255, 255, 255 }");
    }

    [Fact]
    public void A_catch_capture_may_return_a_switch_over_the_error()
    {
        var cs = EmitZig("""
            const E = error{ Truncated, BadStart, Other };
            fn step(x: u8) E!u8 {
                return switch (x) {
                    0 => error.Truncated,
                    1 => error.BadStart,
                    2 => error.Other,
                    else => x * 2,
                };
            }
            fn code(x: u8) u32 {
                const v = step(x) catch |e| return switch (e) {
                    error.Truncated => 900,
                    error.BadStart => 901,
                    else => 902,
                };
                return v;
            }
            pub fn main() u8 {
                return @truncate(code(0) + code(1) + code(2) + code(7));
            }
            """);
        // Task #80 (grammar): `catch |e| return switch (e) { … }`.
        cs.ShouldContain("return (e switch { 1 => 900, 2 => 901, _ => 902 });");
    }

    [Fact]
    public void A_comptime_labeled_block_is_a_static_table()
    {
        var cs = EmitZig("""
            fn classify(c: u8) u8 {
                const xx = 0xF1;
                const as = 0xF0;
                const first = comptime first: {
                    const a: [4]u8 = @splat(as);
                    const b: [4]u8 = @splat(xx);
                    break :first a ++ b;
                };
                return first[c & 7] +% (@as(u8, 1) << @intCast(c & 3));
            }
            pub fn main() u8 {
                return classify(1) +% classify(6);
            }
            """);
        // Task #82: `comptime first: { … }` runs in the comptime interpreter (arrays `++`-joined element by element) into
        // a static, and a shift amount takes a cast builtin's type.
        cs.ShouldContain("__ctblk0 = Libc.GlobalArrayFrom<byte>(new byte[]{ (byte)240, (byte)240, (byte)240, (byte)240, (byte)241, (byte)241, (byte)241, (byte)241 });");
    }

    [Fact]
    public void A_switch_over_a_comptime_bool_selects_its_prong()
    {
        var cs = EmitZig("""
            fn id(comptime T: type, x: T) T {
                return x;
            }
            fn widen(comptime wide: bool, v: u8) u32 {
                const W = switch (wide) {
                    true => u32,
                    false => u8,
                };
                const k: u32 = switch (wide) {
                    true => 1000,
                    false => 1,
                };
                return @as(u32, id(W, v)) + @as(u32, @sizeOf(W)) * k;
            }
            pub fn main() u8 {
                return @truncate(widen(true, 7) + widen(false, 9));
            }
            """);
        // Task #84: each instance folds the switch, as a type alias and as a value.
        cs.ShouldContain("uint k = 1000;");
        cs.ShouldContain("return (uint)id__u32(v)");
        cs.ShouldContain("uint k = 1;");
        cs.ShouldContain("return (uint)id__u8(v)");
    }

    [Fact]
    public void Inline_prongs_type_switch_captures_and_string_literal_anytype_lengths()
    {
        var cs = EmitZig("""
            fn tail(n: usize, bytes: []const u8) u32 {
                var acc: u32 = 7;
                switch (n) {
                    inline 0, 1, 2 => |count| {
                        inline for (0..count) |i| acc = acc *% 31 +% bytes[i];
                        return acc;
                    },
                    inline 3...5 => |count| {
                        acc +%= @as(u32, count) * 1000;
                        inline for (0..count) |i| acc +%= bytes[i];
                        return acc;
                    },
                    else => return 0,
                }
            }
            fn length(x: anytype) usize {
                return x.len;
            }
            fn Widened(comptime T: type) type {
                return switch (T) {
                    comptime_int => u64,
                    else => |U| U,
                };
            }
            pub fn main() u8 {
                const w: Widened(u16) = 40000;
                return @truncate(tail(4, "abcdef") +% @as(u32, @intCast(length("hello"))) +% w);
            }
            """);
        // Task #87: one section per inline case value, its capture a comptime constant; a string literal passed as an
        // `anytype` is its logical `[5]u8` (the stored NUL excluded, it had been 6). Task #86: `else => |U| U` over a type.
        cs.ShouldContain("case 5UL:");
        cs.ShouldContain("length__char_5_(Libc.L(\"hello\\0\"u8))");
        cs.ShouldContain("ushort w = 40000;");
    }

    [Fact]
    public void Extern_struct_value_loops_and_a_struct_const_with_a_computed_array_field()
    {
        var cs = EmitZig("""
            const Kind = union(enum) { null, undefined, int: u8 };
            fn isPow2(v: u16) bool {
                return v != 0 and (v & (v - 1)) == 0;
            }
            fn Words(comptime T: type) type {
                const info = @typeInfo(T);
                if (info != .int) @compileError("an integer");
                if (!isPow2(@bitSizeOf(T))) @compileError("a power of two");
                return extern struct {
                    const Self = @This();
                    const last = ~@as(u64, 0) >> 42;
                    w: [2]T,
                    const full: Self = full: {
                        var w: [2]T = @splat(~@as(T, 0));
                        w[1] = last;
                        break :full .{ .w = w };
                    };
                    fn first(self: Self) ?usize {
                        const word = for (self.w) |word| {
                            if (word != 0) break word;
                        } else return null;
                        return @ctz(word);
                    }
                    fn same(self: Self, other: Self) bool {
                        var i: usize = 0;
                        return while (i < 2) : (i += 1) {
                            if (self.w[i] != other.w[i]) break false;
                        } else true;
                    }
                };
            }
            pub fn main() u8 {
                const k = Kind{ .int = 3 };
                const W = Words(u64);
                const f = W.full;
                return @truncate(k.int + (f.first() orelse 9) + @intFromBool(f.same(f)) + @popCount(f.w[1]));
            }
            """);
        // Task #88 (std.bit_set.Array's shapes). `return extern struct` keeps the C layout; `null`/`undefined` are
        // variant names (the tag member escaped for C#); a `for … else return null` value loop returns from the function
        // on normal completion.
        cs.ShouldContain("LayoutKind.Sequential)]\nunsafe struct Words__u64");
        cs.ShouldContain("@null = 0,");
        cs.ShouldContain("goto __lv0_end;");
        // Two silent miscompiles: `~@as(u64, 0) >> 42` had sign-extended to all ones, and the labeled block's struct had
        // lost its array field (the copy-in ran before the block's locals, and the interpreter skipped it). The const is
        // now built by a synthesized initializer that copies the evaluated array in.
        cs.ShouldContain("Words__u64__full__static = __init_Words__u64__full();");
        cs.ShouldContain("new ulong[]{ 18446744073709551615UL, 4194303UL }");
    }

    [Fact]
    public void Asm_in_a_dead_prong_comptime_array_params_and_implicit_comptime_int_params()
    {
        var cs = EmitZig("""
            const builtin = @import("builtin");

            const Seed = [4]u32;
            const seed_a = Seed{ 3, 5, 7, 11 };

            const Param = struct { a: usize, k: u32 };

            fn Mixer(comptime seed: Seed, rounds: comptime_int) type {
                return struct {
                    const Self = @This();
                    s: [4]u32 align(16),
                    count: u32 align(8) = 0,

                    fn init() Self {
                        return .{ .s = seed };
                    }

                    fn step(self: *Self) void {
                        const params = comptime [_]Param{ .{ .a = 0, .k = 1 }, .{ .a = 2, .k = 3 } };
                        inline for (params) |p| {
                            self.s[p.a] = self.s[p.a] *% 31 +% p.k;
                        }
                        switch (builtin.cpu.arch) {
                            .x86_64, .aarch64 => if (builtin.zig_backend == .stage2_c) {
                                asm volatile ("nop");
                                return;
                            },
                            else => {},
                        }
                        self.count += rounds;
                    }
                };
            }

            pub fn main() u8 {
                var m = Mixer(seed_a, 5).init();
                m.step();
                m.step();
                return @truncate(m.s[0] +% m.s[2] +% m.count);
            }
            """);
        // Task #90 (std.crypto.sha2's shapes). `seed: Seed` (a `[4]u32` alias) is a comptime ARRAY argument read from a
        // const global; `rounds: comptime_int` is comptime without the keyword; `align(16)` fields parse; `inline for`
        // over a comptime array value binds each element as a literal; the `asm` sits in a prong whose condition folds false.
        cs.ShouldContain("public fixed uint s[4];");
        cs.ShouldContain("stackalloc uint[]{ 3u, 5u, 7u, 11u };");
        cs.ShouldContain("Param p = new Param { a = 0UL, k = 1u };");
        cs.ShouldContain("_5_step(&m);");
    }

    [Theory]
    [InlineData("const m = ~@as(u64, 0) >> 88; return @truncate(m);", "type 'u6' cannot represent integer value '88'")]
    [InlineData("var b = true; _ = &b; return @intFromBool(b) * 10;", "type 'u1' cannot represent integer value '10'")]
    [InlineData("var x: u8 = 3; _ = &x; return x + 300;", "type 'u8' cannot represent integer value '300'")]
    [InlineData("var x: i8 = -3; _ = &x; return @bitCast(x - 128);", "type 'i8' cannot represent integer value '128'")]
    public void A_comptime_operand_its_type_cannot_hold_is_rejected_as_zig_does(string body, string message)
    {
        // Task #91: a shift amount outside `Log2Int` of the shifted operand, and an integer literal outside its typed peer's
        // range, are zig compile errors (the messages are zig's own); dotcc had lowered both silently.
        Should.Throw<CompileException>(() => EmitZig("pub fn main() u8 {\n    " + body + "\n}\n")).Message.ShouldContain(message);
    }

    [Theory]
    [InlineData("var x: u64 = 3; _ = &x; return @truncate(x >> 63);")]
    [InlineData("const k = 1 << 40; return @truncate(k >> 33);")]
    [InlineData("var b = true; _ = &b; return @as(u8, @intFromBool(b)) * 10;")]
    public void A_comptime_operand_that_fits_or_is_comptime_int_is_accepted(string body)
    {
        Should.NotThrow(() => EmitZig("pub fn main() u8 {\n    " + body + "\n}\n"));
    }

    [Fact]
    public void A_comptime_block_returning_a_local_slice_is_a_static_of_its_evaluated_elements()
    {
        var cs = EmitZig("""
            const Color = enum(u8) { red = 4, green = 9, blue = 2 };

            inline fn table(comptime fv: []const comptime_int) []const u8 {
                comptime {
                    var result: [fv.len]u8 = undefined;
                    for (&result, fv) |*r, f| {
                        r.* = @intCast(f * 2);
                    }
                    const final = result;
                    return &final;
                }
            }

            inline fn colors(comptime fv: []const comptime_int) []const Color {
                comptime {
                    var result: [fv.len]Color = undefined;
                    for (&result, fv) |*r, f| {
                        r.* = @enumFromInt(f);
                    }
                    const final = result;
                    return &final;
                }
            }

            pub fn main() u8 {
                const t = table(&.{ 5, 6, 7 });
                const c = colors(&.{ 2, 9 });
                return t[0] + t[1] + t[2] + @intFromEnum(c[0]) * 10 + @intFromEnum(c[1]);
            }
            """);
        // Task #89 (std.enums.valuesFromFields' shape): the `comptime { …; return &final; }` block runs in the interpreter and
        // its slice becomes a pinned static. Two silent miscompiles: lowered as runtime code it returned a slice over the
        // frame's `stackalloc` (dangling), and the interpreter had skipped the `const final = result;` array copy (all zeros).
        cs.ShouldContain("return new ConstSlice<byte>(Libc.L(\"\\n\\x0C\\x0E\\0\"u8), 3UL);");
        cs.ShouldContain("new Color[]{ (Color)(byte)2, (Color)(byte)9 }");
        cs.ShouldNotContain("stackalloc Color");
    }

    [Fact]
    public void A_non_exhaustive_enum_has_no_underscore_member_and_reports_its_mode()
    {
        var cs = EmitZig("""
            const E = enum(u8) { a, b, _ };

            pub fn main() u8 {
                const info = @typeInfo(E).@"enum";
                const open: u8 = if (info.mode == .nonexhaustive) 10 else 0;
                const e: E = @enumFromInt(7);
                return open + @as(u8, info.field_names.len) + @intFromEnum(e);
            }
            """);
        // Task #89: `_` marks the enum non-exhaustive; it had been lowered as a member `_ = 2`, so `field_names.len` said 3.
        cs.ShouldNotContain("_ = 2,");
        cs.ShouldContain("byte open = 10;");
        cs.ShouldContain("return (byte)(open + (byte)2 + (byte)e);");
    }

    [Fact]
    public void Wide_comptime_int_arithmetic_folds_and_comptime_case_labels_fold_to_the_subject()
    {
        var cs = EmitZig("""
            fn classify(x: usize) u8 {
                const limit = 4 * 3;
                return switch (x) {
                    limit => 2,
                    else => 3,
                };
            }

            fn pick() u8 {
                if (@inComptime()) return 1;
                return 2;
            }

            pub fn main() u8 {
                const big = 0xFFFF_FFFF_FFFF_FFFF + 1;
                const top: u64 = @intCast(big >> 60);
                return @intCast(top + classify(12) + pick());
            }
            """);
        // Task #83: `maxInt(u64) + 1` is a comptime_int; it had wrapped at the 64-bit carrier to 0.
        cs.ShouldContain("System.Int128 big = System.Int128.Parse(\"18446744073709551616\");");
        // A comptime-const case label folds to a literal at the subject's type.
        cs.ShouldContain("(x switch { 12UL => 2, _ => 3 })");
        // `@inComptime()` is false in a function lowered for runtime.
        cs.ShouldContain("if (Cond.B(false))");
    }

    [Fact]
    public void Struct_builtin_reifies_one_struct_per_instance_with_its_default_values()
    {
        var cs = EmitZig("""
            fn FieldStruct(comptime Data: type, comptime def: ?Data) type {
                const default_ptr: ?*const anyopaque = if (def) |d| @ptrCast(&d) else null;
                return @Struct(.auto, null, &.{ "a", "b", "c" }, &@splat(Data), &@splat(.{ .default_value_ptr = default_ptr }));
            }

            fn Pair(comptime A: type, comptime B: type) type {
                return @Struct(.auto, null, &.{ "x", "y" }, &.{ A, B }, &.{ .{}, .{} });
            }

            fn sum(s: FieldStruct(u8, 7)) u8 {
                return s.a + s.b * 10 + s.c;
            }

            pub fn main() u8 {
                const t: FieldStruct(bool, null) = .{ .a = true, .b = false, .c = true };
                const p: Pair(u8, u16) = .{ .x = 2, .y = 300 };
                const n: u8 = if (t.a and !t.b and t.c) 100 else 0;
                return sum(.{ .b = 3 }) + n + p.x + @as(u8, @intCast(p.y - 290));
            }
            """);
        // Task #93: `@Struct` as a type-returning function's result is a struct named after the instance; a literal
        // omitting a field is filled from the `.default_value_ptr` default (7), and a null default leaves none.
        cs.ShouldContain("new FieldStruct__u8_opt7 { b = 3, a = 7, c = 7 }");
        cs.ShouldContain("new FieldStruct__bool_optnull { a = true, b = false, c = true }");
        cs.ShouldContain("unsafe struct Pair__u8_u16");
        cs.ShouldContain("public ushort y;");
    }

    [Theory]
    [InlineData("&.{ \"a\", \"a\" }", "&@splat(u8)", "&@splat(.{})", "duplicate struct field name 'a'")]
    [InlineData("&.{ \"a\", \"b\" }", "&.{ u8 }", "&@splat(.{})", "field_types has 1 element(s) but there are 2 field name(s)")]
    [InlineData("&.{ \"a\", \"b\" }", "&@splat(u8)", "&@splat(.{ .@\"comptime\" = true })", "a `comptime` field is not modeled")]
    public void Struct_builtin_rejects_a_malformed_field_list(string names, string types, string attrs, string message)
    {
        // Task #93: zig rejects a duplicate name and a list whose length is not the name count; a comptime field is a cut.
        var ex = Should.Throw<Exception>(() => EmitZig(
            "fn S() type {\n    return @Struct(.auto, null, " + names + ", " + types + ", " + attrs + ");\n}\n"
            + "pub fn main() u8 {\n    const s: S() = undefined;\n    _ = s;\n    return 0;\n}\n"));
        ex.Message.ShouldContain(message);
    }

    [Fact]
    public void Tag_name_of_a_comptime_enum_value_is_its_member_name()
    {
        var cs = EmitZig("""
            const E = enum { a, bb, ccc };
            fn keyFor(i: usize) E {
                return @enumFromInt(i);
            }
            pub fn main() u8 {
                var total: u8 = 0;
                inline for (0..3) |i| {
                    const key = comptime keyFor(i);
                    const tag = @tagName(key);
                    total += @intCast(tag.len * 10 + i);
                }
                return total + @as(u8, @intCast(@tagName(E.bb).len));
            }
            """);
        // Task #93: `@tagName` of a comptime key (`comptime keyFor(i)`) is the member's name as a string literal; the
        // enum-typed fold is cast to the enum (it had been spliced as `E key = 0UL;`, which C# rejects).
        cs.ShouldContain("E key = (E)0;");
        cs.ShouldContain("Libc.L(\"bb\\0\"u8)");
        cs.ShouldContain("Libc.L(\"ccc\\0\"u8)");
    }

    [Fact]
    public void Tag_name_of_a_runtime_enum_value_is_a_loud_cut()
    {
        Should.Throw<Exception>(() => EmitZig("""
            const E = enum { a, b };
            pub fn main() u8 {
                var e: E = .a;
                e = .b;
                return @intCast(@tagName(e).len);
            }
            """)).Message.ShouldContain("`@tagName` is modeled for a comptime-known enum value");
    }

    [Fact]
    public void A_literal_of_another_modules_struct_fills_its_field_defaults()
    {
        // A struct literal in the root of a struct another module declares had dropped every omitted default (dotcc 3,
        // zig 62): the defaults are the declaring module's, and are lowered there.
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zigdef-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "m.zig"),
                "pub const S = struct {\n    a: u8 = 5,\n    b: u8,\n};\n"
                + "pub fn Gen(comptime n: u8) type {\n    return struct { k: u8 = n, z: u8 = 1 };\n}\n");
            var main = Path.Combine(dir, "main.zig");
            File.WriteAllText(main,
                "const m = @import(\"m.zig\");\npub fn main() u8 {\n    const s: m.S = .{ .b = 1 };\n"
                + "    const g: m.Gen(9) = .{ .z = 2 };\n    return s.a * 10 + s.b + g.k + g.z;\n}\n");
            var cs = Compiler.EmitCSharp(new[] { main });
            cs.ShouldContain("new m__S { b = 1, a = 5 }");
            cs.ShouldContain("new m__Gen__9 { z = 2, k = 9 }");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_by_ref_if_capture_points_at_the_payload_in_place()
    {
        var cs = EmitZig("""
            const Holder = struct {
                a: ?u8 = 5,
                b: ?u16 = null,
            };

            fn maybe(n: u8) ?u8 {
                return if (n > 2) n else null;
            }

            pub fn main() u8 {
                var x: ?u8 = 10;
                if (x) |*v| {
                    v.* += 3;
                }
                var h: Holder = .{};
                if (h.a) |*p| {
                    p.* *= 2;
                } else {
                    h.a = 99;
                }
                if (h.b) |*q| {
                    q.* = 1;
                } else {
                    h.b = 7;
                }
                var n: u8 = 4;
                var ptr: ?*u8 = &n;
                if (ptr) |*pp| {
                    pp.*.* += 1;
                    const same: *u8 = pp.*;
                    same.* += 10;
                }
                var seen: u8 = 0;
                if (maybe(9)) |*m| {
                    seen = m.*;
                }
                const got: u8 = if (ptr == null) 1 else 0;
                return x.? + h.a.? + @as(u8, @intCast(h.b.?)) + n + seen + got;
            }
            """);
        // Task #94: `if (opt) |*v|` points `v` INTO the optional (no copy, no write-back), an optional pointer's capture
        // is the address of the pointer variable, and an rvalue condition is a temporary the capture points into.
        cs.ShouldContain("byte* v = ZigMem.OptionalPayload(&x);");
        cs.ShouldContain("byte* p = ZigMem.OptionalPayload(&h.a);");
        cs.ShouldContain("byte** pp = &ptr;");
        cs.ShouldContain("byte* m = ZigMem.OptionalPayload(&__cap);");
    }

    [Fact]
    public void A_by_ref_capture_of_an_error_union_is_a_loud_cut()
    {
        Should.Throw<Exception>(() => EmitZig("""
            fn get(ok: bool) !u8 {
                return if (ok) 3 else error.Nope;
            }
            pub fn main() u8 {
                var total: u8 = 0;
                if (get(true)) |*v| {
                    total = v.*;
                }
                return total;
            }
            """)).Message.ShouldContain("a by-ref capture of an error union's payload is not modeled");
    }

    [Theory]
    [InlineData("const y: u8 = 1;\n    y = 2;\n    return y;")]
    [InlineData("const y: u8 = 1;\n    y += 2;\n    return y;")]
    [InlineData("const y: u8 = 1;\n    const p = &y;\n    p.* = 2;\n    return y;")]
    [InlineData("const s: S = .{ .a = 1 };\n    s.a = 2;\n    return s.a;")]
    [InlineData("const arr = [_]u8{ 1, 2 };\n    arr[0] = 5;\n    return arr[0];")]
    [InlineData("const x: ?u8 = 1;\n    if (x) |*v| {\n        v.* = 2;\n    }\n    return x.?;")]
    [InlineData("return f(1);")]
    [InlineData("var y: u8 = 1;\n    g(&y);\n    return y;")]
    public void A_store_to_a_const_is_rejected_as_in_zig(string body)
    {
        // Task #95: zig's "cannot assign to constant" for a `const` local, a parameter, a field or element of one, and a
        // store through a pointer to const (`&y` of a const `y` is a `*const T`; so is a by-ref capture of a const).
        Should.Throw<CompileException>(() => EmitZig(
            "const S = struct { a: u8 };\n"
            + "fn f(x: u8) u8 {\n    x = 2;\n    return x;\n}\n"
            + "fn g(p: *const u8) void {\n    p.* = 2;\n}\n"
            + "pub fn main() u8 {\n    " + body + "\n}\n")).Message.ShouldContain("cannot assign to constant");
    }

    [Fact]
    public void A_store_through_a_pointer_to_mutable_storage_still_lowers()
    {
        // Task #95: a `const` binding that HOLDS a pointer (`const p: *[2]u32 = &b;`), a pointer parameter, a `var`, and a
        // by-ref capture of a `var` optional all write their pointee, as in zig (this program returns 10 there).
        var cs = EmitZig("""
            fn fill(buf: *[2]u32, v: u32) void {
                buf[1] = v;
            }
            pub fn main() u8 {
                var b = [2]u32{ 0, 0 };
                const p: *[2]u32 = &b;
                p[1] = 3;
                const q = &b;
                q[0] = 4;
                p.*[0] += 1;
                fill(&b, 3);
                var o: ?u8 = 1;
                if (o) |*v| {
                    v.* += 1;
                }
                return @intCast(b[0] + b[1] + o.?);
            }
            """);
        cs.ShouldContain("buf[1] = v;");
        cs.ShouldContain("ZigMem.OptionalPayload(&o)");
    }

    [Fact]
    public void An_empty_literal_at_a_nonzero_extent_is_still_rejected()
    {
        Should.Throw<Exception>(() => EmitZig("""
            pub fn main() u8 {
                const a: [3]u8 = [3]u8{};
                return a[0];
            }
            """)).Message.ShouldContain("empty array literal");
    }
}
