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
