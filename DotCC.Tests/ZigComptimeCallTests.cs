#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for the comptime-call engine V1 (road-to-zig-std G3): a function whose return type is
/// <c>comptime_int</c> (<c>std.math.maxInt</c> / <c>minInt</c>) has no runtime existence, so every call is
/// a deferred comptime fold the shared interpreter resolves once every module has drained, and the
/// instance bodies are dropped from the emitted program. End-to-end in the <c>comptime_int_calls</c>
/// zig-oracle program and, against real std, <c>Dotcc_matches_zig_std_math_max_min_int_from_source</c>.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigComptimeCallTests
{
    private const string MathSource = """
        pub fn maxInt(comptime T: type) comptime_int {
            const info = @typeInfo(T).int;
            return (1 << (info.bits - @intFromBool(info.signedness == .signed))) - 1;
        }
        pub fn minInt(comptime T: type) comptime_int {
            const info = @typeInfo(T).int;
            return switch (info.signedness) {
                .unsigned => 0,
                .signed => -(1 << (info.bits - 1)),
            };
        }
        """;

    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigcc-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_comptime_int_call_folds_and_its_instance_is_not_emitted()
    {
        var cs = EmitZig(MathSource + """

            fn span(comptime T: type) comptime_int {
                return maxInt(T) - minInt(T);
            }
            pub fn main() u8 {
                const a: u8 = maxInt(u8);
                const b: i16 = minInt(i16);
                const c: u64 = maxInt(u64);
                _ = b;
                _ = c;
                return if (span(i8) == 255) a - 213 else 0;
            }
            """);
        cs.ShouldContain("byte a = (byte)((System.Int128)255UL);");
        cs.ShouldContain("short b = (short)(-(System.Int128)32768UL);");
        cs.ShouldContain("ulong c = (ulong)((System.Int128)18446744073709551615UL);");
        cs.ShouldContain("(System.Int128)255UL == 255");   // span(i8), a comptime-only call inside another
        cs.ShouldNotContain("maxInt__");
        cs.ShouldNotContain("minInt__");
        cs.ShouldNotContain("span__");
    }

    [Fact]
    public void A_comptime_int_call_into_a_lazy_module_folds_after_the_module_drains()
    {
        // The callee's instance body lowers only in the module graph's drain, after the root's own
        // passes, so the fold must resolve after that (not in the root's pass 3).
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zigcc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "math.zig"), MathSource);
            var main = Path.Combine(dir, "main.zig");
            File.WriteAllText(main, """
                const math = @import("math.zig");
                pub fn main() u8 {
                    var x: u32 = 70000;
                    _ = &x;
                    const lo: i8 = math.minInt(i8);
                    return if (x > math.maxInt(u16) and lo == -128) 42 else 0;
                }
                """);
            var cs = Compiler.EmitCSharp(new[] { main });
            cs.ShouldContain("x > (System.Int128)65535UL");
            cs.ShouldContain("sbyte lo = (sbyte)(-(System.Int128)128UL);");
            cs.ShouldNotContain("maxInt__");
            cs.ShouldNotContain("minInt__");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
