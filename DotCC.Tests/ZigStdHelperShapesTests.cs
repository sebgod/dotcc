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
