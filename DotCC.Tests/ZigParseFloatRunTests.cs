#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for the last walls between std.fmt.parseFloat and a run that equals zig bit for bit (road-to-zig-std, task #59),
/// three of them silent wrong answers: an untyped literal on the left of <c>&lt;&lt;</c> is a comptime_int, so it widens
/// to the carrier (C#'s <c>int</c> shifted <c>1 &lt;&lt; 52</c> by 52 MOD 32); <c>opt orelse error.E</c> is an error union
/// (the error's flat code was returned as the payload: <c>parseFloat("abc")</c> gave 10.0); and <c>buf.* = @bitCast(v)</c>
/// through a <c>*[N]u8</c> stores the bytes (it assigned the pointer). Also: a bare <c>while (true)</c> (CS0161), an
/// <c>f32</c> literal table (CS0664), a type alias chosen by a <c>switch (T)</c> keeping its declared width,
/// <c>Gen(T).CONST</c> as a value, and <c>break :blk switch (x) { 0 =&gt; break, … }</c>. End to end in the
/// <c>parse_float_core</c> zig-oracle program and the real-std parseFloat differential.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigParseFloatRunTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigpfrun-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private const string Program = """
        const E = error{Bad};

        fn bits(comptime T: type) comptime_int {
            return switch (@typeInfo(T).float.bits) {
                32 => 32,
                64 => 64,
                else => @compileError("no"),
            };
        }

        fn widthOf(comptime Type: type) u32 {
            const R = switch (Type) {
                else => Type,
                comptime_float => f64,
            };
            return bits(R);
        }

        fn shifted(n: u6) u64 {
            return (1 << 52) | (@as(u64, 1) << n);
        }

        fn half(x: ?u8) E!u8 {
            return x orelse error.Bad;
        }

        fn firstBig(xs: []const u8) u8 {
            var i: usize = 0;
            while (true) {
                if (xs[i] > 10) return xs[i];
                i += 1;
            }
        }

        fn store(buf: *[8]u8, v: u64) void {
            buf.* = @bitCast(v);
        }

        fn Table(comptime T: type) type {
            return struct {
                pub const len = if (T == f32) 3 else 5;
            };
        }

        fn pick(xs: []const u8) u8 {
            var i: usize = 0;
            var total: u8 = 0;
            while (i < xs.len) : (i += 1) {
                const add: u8 = blk: {
                    break :blk switch (xs[i]) {
                        0 => break,
                        1, 2 => 10,
                        else => 1,
                    };
                };
                total += add;
            }
            return total;
        }

        pub fn main() u8 {
            const pows = [_]f32{ 1e0, 1e1, 1e2 };
            var buf: [8]u8 = undefined;
            store(&buf, 0x0102030405060708);
            var total: u64 = 0;
            total += widthOf(f32) + widthOf(f64);
            total += (shifted(3) >> 52) + (shifted(3) & 0xff);
            total += half(7) catch 0;
            total += half(null) catch 100;
            total += firstBig(&.{ 1, 2, 30, 4 });
            total += buf[0] + buf[7];
            total += Table(f32).len + Table(f64).len;
            total += @intFromFloat(pows[2]);
            total += pick(&.{ 1, 2, 3, 0, 1 });
            return @intCast(total % 256);
        }
        """;

    [Fact]
    public void An_untyped_literal_shifted_by_a_wide_count_widens_to_the_comptime_int_carrier()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("return (ulong)((System.Int128)1UL << 52 | (ulong)1 << (int)(n));");
    }

    [Fact]
    public void Optional_orelse_an_error_is_an_error_union()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("return ErrUnion.OrError(x, 1);");
    }

    [Fact]
    public void A_bit_cast_stored_through_a_pointer_to_array_writes_its_bytes()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("System.Runtime.CompilerServices.Unsafe.WriteUnaligned(buf, v);");
    }

    [Fact]
    public void A_while_true_renders_bare_and_an_f32_table_takes_float_literals()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("while (true)");
        cs.ShouldContain("float* __cl0 = stackalloc float[]{ 1e0F, 1e1F, 1e2F };");
    }

    [Fact]
    public void A_type_alias_chosen_by_a_type_switch_keeps_its_declared_width()
    {
        var cs = EmitZig(Program);
        cs.ShouldMatch(@"uint widthOf__f64\(\)\s*\{\s*return 64;");
    }

    [Fact]
    public void A_break_value_switch_with_a_jump_prong_lowers_as_a_statement()
    {
        var cs = EmitZig(Program);
        cs.ShouldMatch(@"case 0:\s*goto __loop\d+_swbrk;");
    }
}
