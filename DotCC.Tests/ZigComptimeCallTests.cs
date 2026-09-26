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
        // Evaluated when instantiated (the immediate path), so the literal splices straight in.
        cs.ShouldContain("byte a = 255;");
        cs.ShouldContain("short b = (short)(-32768);");
        cs.ShouldContain("ulong c = 18446744073709551615UL;");   // a comptime_int past `long` is typed to fit
        cs.ShouldContain("(255 == 255)");   // span(i8), a comptime-only call inside another
        cs.ShouldNotContain("maxInt__");
        cs.ShouldNotContain("minInt__");
        cs.ShouldNotContain("span__");
    }

    [Fact]
    public void A_comptime_int_call_into_a_lazy_module_folds()
    {
        // Evaluated when instantiated in its own module; a body the immediate path cannot evaluate would
        // stay a deferred fold, resolved after the module graph drains (not in the root's pass 3).
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
            cs.ShouldContain("x > (uint)(65535)");
            cs.ShouldContain("sbyte lo = (sbyte)(-128);");
            cs.ShouldNotContain("maxInt__");
            cs.ShouldNotContain("minInt__");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
