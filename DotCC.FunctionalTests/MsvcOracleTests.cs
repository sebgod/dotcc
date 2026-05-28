#nullable enable

using System.IO;
using System.Linq;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// Differential tests: for each fixture, compile + run with MSVC (the
/// oracle) AND compile + run with dotcc → Roslyn, then assert the two
/// stdouts agree. When MSVC isn't available on the host (non-Windows,
/// no VS install), tests are skipped — they assert nothing dotcc can't
/// already validate against the hand-written <c>expected-stdout.txt</c>.
/// </summary>
/// <remarks>
/// The point of the oracle is to catch divergences between dotcc's
/// emitted-C# semantics and real C. Examples worth catching: <c>printf</c>
/// format-spec edge cases (precision rounding, <c>%g</c> vs <c>%f</c>),
/// integer overflow behaviour, pointer-arithmetic quirks, evaluation-order
/// surprises. Any new fixture goes through this test automatically — no
/// per-fixture wiring.
/// </remarks>
public sealed class MsvcOracleTests
{
    public static System.Collections.Generic.IEnumerable<object[]> Fixtures =>
        FixtureRunner.Discover().Select(f => new object[] { f.name, f.dir });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Dotcc_matches_msvc_output(string name, string dir)
    {
        if (!MsvcOracle.IsAvailable)
        {
            Assert.Skip($"MSVC oracle not available on this host (vswhere didn't find a VC install). Skipping {name}.");
        }

        var match = FixtureRunner.Discover().Single(f => f.name == name);

        // dotcc path: emit C# (csproj-shaped — Roslyn doesn't want the
        // #:property header), compile in-process, run with captured stdout.
        var emitted = Compiler.EmitCSharp(
            inputPaths: match.sources,
            includeDirs: new[] { dir },
            defines: null,
            fileBased: false);
        var dotccOut = FixtureRunner.CompileAndRun(emitted, System.Array.Empty<string>())
            .ReplaceLineEndings("\n").TrimEnd('\n');

        // MSVC path: copy sources to an isolated work dir, build with cl,
        // run, capture stdout.
        var workDir = Path.Combine(
            Path.GetTempPath(),
            $"dotcc-msvc-{name}-{System.Guid.NewGuid():N}");
        var msvcOut = MsvcOracle.CompileAndRun(match.sources, workDir)
            .ReplaceLineEndings("\n").TrimEnd('\n');

        // Differential assertion — dotcc's output should match MSVC's
        // byte-for-byte. If this fails, dotcc's emitter or libc has diverged
        // from real-C semantics on this fixture.
        dotccOut.ShouldBe(msvcOut);
    }
}
