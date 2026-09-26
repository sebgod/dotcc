#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for the declared integer width a VALUE carries (road-to-zig-std G3, the wall std.fmt.parseInt
/// and std.mem.sort shared): <c>@typeInfo(@TypeOf(x)).int.bits</c> over an <c>anytype</c> argument is
/// answered from the width the argument's value was spelled with (a typed local, a parameter, a capture
/// over a spelled element type, a <c>.len</c>, a call's declared return), and a width other than the
/// lowered type's own keys its own instance. Also pins <c>comptime assert</c>, <c>@disableInstrumentation()</c>
/// and the backend bracing a folded-away arm (it had left a bare <c>else</c> absorbing the next statement).
/// End-to-end, against real std, in <c>Dotcc_matches_zig_std_fmt_parse_int_from_source</c>.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigValueWidthTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigvw-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private const string Bits = """
        fn bitsOf(x: anytype) u16 {
            return @typeInfo(@TypeOf(x)).int.bits;
        }
        fn len(s: []const u8) usize {
            return s.len;
        }
        fn sum(xs: []const u5) u16 {
            var t: u16 = 0;
            for (xs) |e| t += bitsOf(e);
            return t;
        }
        """;

    [Fact]
    public void An_anytype_arguments_spelled_width_answers_its_bits()
    {
        var cs = EmitZig(Bits + """

            pub fn main() u8 {
                const a: u21 = 5;
                const b: u32 = 5;
                const s = "abc";
                var total: u16 = bitsOf(a) + bitsOf(b) + bitsOf(s.len);
                const fives = [_]u5{ 1, 2 };
                total += sum(&fives);
                total += bitsOf(len(s));
                return @intCast(total - 154);
            }
            """);
        cs.ShouldContain("bitsOf__u32w21(");   // u21 lowers to uint, but keys its own instance
        cs.ShouldContain("bitsOf__u32(");
        cs.ShouldMatch(@"ushort bitsOf__u32w21\(uint x\)\s*\{\s*return 21;");
        cs.ShouldMatch(@"ushort bitsOf__u64\(ulong x\)\s*\{\s*return 64;");
    }

    [Fact]
    public void A_comptime_assert_is_checked_at_lowering_time()
    {
        var ok = EmitZig("""
            fn assert(ok: bool) void {
                if (!ok) unreachable;
            }
            fn f(comptime T: type) u8 {
                comptime assert(@typeInfo(T) == .int);
                @disableInstrumentation();
                return 42;
            }
            pub fn main() u8 {
                return f(u8);
            }
            """);
        ok.ShouldNotContain("assert(Cond");
        ok.ShouldNotMatch(@"byte f__u8\(\)\s*\{\s*assert");
        var ex = Should.Throw<CompileException>(() => EmitZig("""
            fn assert(ok: bool) void {
                if (!ok) unreachable;
            }
            fn f(comptime T: type) u8 {
                comptime assert(@typeInfo(T) == .int);
                return 42;
            }
            pub fn main() u8 {
                return f(bool);
            }
            """));
        ex.Message.ShouldContain("`comptime assert(…)` failed");
    }

    [Fact]
    public void A_folded_away_else_arm_is_braced_and_does_not_absorb_the_next_statement()
    {
        var cs = EmitZig("""
            fn step(comptime neg: bool, acc: u8, d: u8) u8 {
                var a = acc;
                if (a != 0) {
                    a *= 10;
                } else if (neg) {
                    a = 99;
                }
                a += d;
                return a;
            }
            pub fn main() u8 {
                return step(false, 4, 2);
            }
            """);
        cs.ShouldMatch(@"else\s*\{\s*\}");
    }
}
