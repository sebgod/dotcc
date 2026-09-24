#nullable enable

using System.Runtime.InteropServices;
using DotCC.Frontends;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for the synthetic <c>builtin</c> module's CPU under the target-identity segment. T3a: with a real std to
/// navigate, the architecture is spelled with the type zig's own builtin gives it, <c>@as(std.Target.Cpu.Arch,
/// .x86_64)</c>. T3: on an x86_64 or aarch64 host the whole <c>cpu</c> is a typed <c>std.Target.Cpu</c>, its model
/// and feature set read off the host through .NET's intrinsics, as zig's <c>-mcpu=native</c> does. Without a std it
/// stays the bare duck-typed tag, and every comptime question folds exactly as before. End-to-end in the real-std
/// differentials <c>Dotcc_matches_zig_std_builtin_cpu_arch_from_source</c> and
/// <c>Dotcc_matches_zig_std_builtin_cpu_features_from_source</c>.
/// </summary>
public sealed class ZigSyntheticBuiltinTargetTests
{
    [Fact]
    public void With_a_std_root_the_cpu_is_a_typed_std_Target_Cpu_read_off_the_host()
    {
        var source = ZigSyntheticModules.SourceForPath(ZigSyntheticModules.BuiltinPath, withStd: true);
        source.ShouldContain("const std = @import(\"std\");");
        switch (RuntimeInformation.ProcessArchitecture)
        {
            case Architecture.X64:
                source.ShouldMatch(@"pub const cpu: std\.Target\.Cpu = \.\{\s*\.arch = @as\(std\.Target\.Cpu\.Arch, \.x86_64\),"
                    + @"\s*\.model = &std\.Target\.x86\.cpu\.x86_64(_v[234])?,"
                    + @"\s*\.features = std\.Target\.x86\.featureSet\(&\.\{ \.@""64bit"", \.cmov, ");
                source.ShouldContain(".sse2");   // part of every x86-64
                // A feature is listed exactly when .NET reports it.
                source.Contains(".avx2,").ShouldBe(System.Runtime.Intrinsics.X86.Avx2.IsSupported);
                break;
            case Architecture.Arm64:
                source.ShouldMatch(@"pub const cpu: std\.Target\.Cpu = \.\{\s*\.arch = @as\(std\.Target\.Cpu\.Arch, \.aarch64\),"
                    + @"\s*\.model = &std\.Target\.aarch64\.cpu\.generic,"
                    + @"\s*\.features = std\.Target\.aarch64\.featureSet\(&\.\{ \.fp_armv8");
                break;
            default:
                source.ShouldMatch(@"pub const cpu = \.\{ \.arch = @as\(std\.Target\.Cpu\.Arch, \.\w+\) \};");
                break;
        }
        source.ShouldMatch(@"pub const target = \.\{ \.cpu = \.\{ \.arch = @as\(std\.Target\.Cpu\.Arch, \.\w+\) \}");
    }

    [Fact]
    public void Without_a_std_root_the_architecture_stays_a_bare_tag()
    {
        var source = ZigSyntheticModules.SourceForPath(ZigSyntheticModules.BuiltinPath);
        source.ShouldNotContain("@import(\"std\")");
        source.ShouldMatch(@"pub const cpu = \.\{ \.arch = \.\w+ \};");
    }
}
