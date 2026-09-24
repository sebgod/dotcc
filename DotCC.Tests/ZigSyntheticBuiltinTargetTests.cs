#nullable enable

using DotCC.Frontends;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for the synthetic <c>builtin</c> module's architecture under the target-identity segment (T3a): with a
/// real std to navigate, <c>cpu.arch</c> is spelled with the type zig's own builtin gives it,
/// <c>@as(std.Target.Cpu.Arch, .x86_64)</c>, so <c>builtin.cpu.arch.endian()</c> is Target.zig's method; without
/// one it stays the bare duck-typed tag, and every comptime question folds exactly as before. End-to-end in the
/// real-std differential <c>Dotcc_matches_zig_std_builtin_cpu_arch_from_source</c>.
/// </summary>
public sealed class ZigSyntheticBuiltinTargetTests
{
    [Fact]
    public void With_a_std_root_the_architecture_is_typed_as_std_Target_Cpu_Arch()
    {
        var source = ZigSyntheticModules.SourceForPath(ZigSyntheticModules.BuiltinPath, withStd: true);
        source.ShouldContain("const std = @import(\"std\");");
        source.ShouldMatch(@"pub const cpu = \.\{ \.arch = @as\(std\.Target\.Cpu\.Arch, \.\w+\) \};");
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
