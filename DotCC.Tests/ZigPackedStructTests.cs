#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for what std.bit_set needed (road-to-zig-std, task #61): <c>packed struct(u8) { … }</c> with a backing integer,
/// a type-returning function's <c>return packed struct(u16) { … }</c> (std.bit_set.Integer), a comparison against the
/// zero-width <c>u0</c> that dotcc does not lower (<c>if (MaskInt == u0) return 0;</c>), the prefix wrapping negation
/// <c>-%x</c> (std.math.boolMask), an integer widened into a <c>?usize</c> return (<c>return @ctz(mask);</c>), and a
/// constant <c>~@as(u8, 0)</c> narrowed under <c>unchecked</c> (C# evaluates <c>~</c> promoted to <c>int</c>). End to end
/// in the <c>packed_struct_backed</c> zig-oracle program and the real-std bit_set differential.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigPackedStructTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigpacked-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private const string Program = """
        const Flags = packed struct(u8) {
            read: u1,
            write: u1,
            mode: u6,
        };

        fn Mask(comptime n: u16) type {
            return packed struct(u16) {
                const Self = @This();
                pub const MaskInt = u16;
                bits: MaskInt,

                pub fn count(self: Self) usize {
                    if (MaskInt == u0) return 0;
                    _ = n;
                    return @popCount(self.bits);
                }

                pub fn first(self: Self) ?usize {
                    if (self.bits == 0) return null;
                    return @ctz(self.bits);
                }
            };
        }

        fn fill(comptime T: type, on: bool) T {
            return -%@as(T, @intFromBool(on));
        }

        pub fn main() u8 {
            const f = Flags{ .read = 1, .write = 0, .mode = 5 };
            const raw: u8 = @bitCast(f);
            const m = Mask(16){ .bits = 0b1011000 };
            const full = fill(u8, true);
            const none = fill(u8, false);
            const all: u8 = ~@as(u8, 0);
            return raw + @as(u8, @intCast(m.count())) + @as(u8, @intCast(m.first().?)) + (full - 250) + none + (all - 255);
        }
        """;

    [Fact]
    public void A_backed_packed_struct_and_a_returned_one_are_byte_packed()
    {
        var cs = EmitZig(Program);
        cs.ShouldMatch(@"StructLayout\(System\.Runtime\.InteropServices\.LayoutKind\.Sequential, Pack = 1\)\]\s*unsafe struct Flags");
        cs.ShouldMatch(@"StructLayout\(System\.Runtime\.InteropServices\.LayoutKind\.Sequential, Pack = 1\)\]\s*unsafe struct Mask__16");
    }

    [Fact]
    public void A_comparison_against_u0_folds_away()
    {
        var cs = EmitZig(Program);
        cs.ShouldMatch(@"ulong Mask__16_count\(Mask__16 self\)\s*\{\s*;\s*return \(ulong\)\(ZigMath\.PopCount\(self\.bits\)\);");
    }

    [Fact]
    public void Prefix_wrapping_negation_is_zero_minus_the_operand()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("return (byte)(0 - (byte)(int)on);");
    }

    [Fact]
    public void An_integer_is_widened_into_an_optional_return()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("return (ulong)ZigMath.Ctz(self.bits);");
    }

    [Fact]
    public void A_constant_complement_narrows_under_unchecked()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("unchecked(");
        cs.ShouldNotContain("byte all = (byte)(~(byte)0);");
    }
}
