#nullable enable

using System;
using System.IO;
using System.Linq;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// Opt-in differential test against gcc (in WSL). For each fixture, compile +
/// run the C source with gcc and assert the output matches the committed
/// <c>expected-stdout.txt</c> baseline AND dotcc's emit. The gcc companion to
/// <see cref="MsvcOracleTests"/> — an independent second reference compiler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design — snapshot testing, not live differential</b>: identical to the
/// MSVC oracle. The fast <see cref="FixtureTests"/> already asserts dotcc's
/// emit matches each fixture's <c>expected-stdout.txt</c> (the snapshot). This
/// oracle adds "gcc still agrees with that snapshot". Spawning <c>wsl.exe</c>
/// per fixture is slow and host-specific, so it's skipped by default.
/// </para>
/// <para>
/// <b>Modes</b> (env vars):
/// <list type="bullet">
/// <item><b>Default</b> (no env var): tests skip with a hint.</item>
/// <item><b><c>DOTCC_RUN_GCC_ORACLE=1</c></b>: compile + run each fixture with
///   gcc; assert gcc's output matches the committed snapshot AND dotcc.</item>
/// <item><b><c>DOTCC_RUN_GCC_ORACLE=1</c> + <c>DOTCC_REGEN_BASELINE=1</c></b>:
///   when gcc's output differs from the snapshot, rewrite the source-tree
///   <c>expected-stdout.txt</c>. Unlike the MSVC oracle, regen here requires
///   the gcc run flag too — so a plain <c>DOTCC_REGEN_BASELINE=1</c> (which
///   drives the MSVC oracle) doesn't silently hand baseline authority to gcc.</item>
/// </list>
/// </para>
/// <para>
/// When gcc/WSL isn't reachable and the gcc mode was requested, tests skip
/// with a clear message rather than fail.
/// </para>
/// </remarks>
public sealed class GccWslOracleTests
{
    private const string RunGccEnv = "DOTCC_RUN_GCC_ORACLE";
    private const string RegenBaselineEnv = "DOTCC_REGEN_BASELINE";

    private static bool GccRunRequested =>
        Environment.GetEnvironmentVariable(RunGccEnv) == "1";
    // Regen only when the gcc run was explicitly requested — keeps the shared
    // DOTCC_REGEN_BASELINE flag from making gcc clobber a baseline a plain
    // MSVC regen meant to author.
    private static bool RegenRequested =>
        GccRunRequested && Environment.GetEnvironmentVariable(RegenBaselineEnv) == "1";

    public static System.Collections.Generic.IEnumerable<object[]> Fixtures =>
        FixtureRunner.Discover().Select(f => new object[] { f.name, f.dir });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Dotcc_matches_gcc_output(string name, string dir)
    {
        if (!GccRunRequested)
        {
            Assert.Skip(
                $"gcc/WSL oracle is opt-in. Set {RunGccEnv}=1 to verify gcc still " +
                $"matches the committed expected-stdout.txt (add {RegenBaselineEnv}=1 " +
                $"to refresh the baseline from gcc). The fast FixtureTests already " +
                $"asserts dotcc's emit against expected-stdout.txt.");
        }
        if (!GccWslOracle.IsAvailable)
        {
            Assert.Skip(
                $"{RunGccEnv} requested but gcc/WSL oracle not available on this host " +
                $"(no wsl.exe, or the default distro has no gcc).");
        }

        // Per-fixture opt-out: a `no-gcc-oracle.txt` sidecar marks a fixture
        // whose output legitimately can't match gcc on this platform — e.g.
        // anything depending on `long double` width, which is ABI-defined
        // (128-bit on Linux/arm64, but dotcc maps `long double` → C# `double`,
        // matching MSVC's `long double == double`). The file's contents are
        // the human-readable reason. Symmetric room for `no-msvc-oracle.txt`
        // later if MSVC needs the same escape hatch.
        var gccSkipMarker = Path.Combine(dir, "no-gcc-oracle.txt");
        if (File.Exists(gccSkipMarker))
        {
            Assert.Skip($"fixture '{name}' opts out of the gcc oracle: " +
                File.ReadAllText(gccSkipMarker).Trim());
        }

        var match = FixtureRunner.Discover().Single(f => f.name == name);

        // dotcc path: emit C# (csproj-shaped — Roslyn doesn't want the
        // #:property header), compile in-process, run with captured stdout.
        var emitted = Compiler.EmitCSharp(
            inputPaths: match.sources,
            includeDirs: new[] { dir },
            defines: null,
            fileBased: false);
        var dotccOut = FixtureRunner.CompileAndRun(emitted, Array.Empty<string>())
            .ReplaceLineEndings("\n").TrimEnd('\n');

        // gcc path: copy sources to an isolated work dir, build with gcc in
        // WSL, run, capture stdout.
        var workDir = Path.Combine(
            Path.GetTempPath(),
            $"dotcc-gcc-{name}-{Guid.NewGuid():N}");
        var gccOut = GccWslOracle.CompileAndRun(match.sources, workDir)
            .ReplaceLineEndings("\n").TrimEnd('\n');

        // dotcc must match gcc byte-for-byte. A divergence means dotcc's
        // emitter / libc has drifted from real-C semantics on this fixture.
        dotccOut.ShouldBe(gccOut, $"dotcc emit diverges from gcc on fixture '{name}'");

        // Snapshot check: the committed expected-stdout.txt should agree with
        // gcc. In regen mode rewrite it; otherwise surface the drift.
        var snapshotRuntimePath = Path.Combine(dir, "expected-stdout.txt");
        var snapshot = File.ReadAllText(snapshotRuntimePath)
            .ReplaceLineEndings("\n").TrimEnd('\n');
        if (snapshot == gccOut)
        {
            return;
        }

        if (RegenRequested)
        {
            var sourcePath = ResolveSourceSnapshotPath(name)
                ?? throw new InvalidOperationException(
                    $"Could not resolve source-tree expected-stdout.txt for fixture '{name}'.");
            File.WriteAllText(sourcePath, gccOut + "\n");
            File.WriteAllText(snapshotRuntimePath, gccOut + "\n");
            return;
        }

        snapshot.ShouldBe(gccOut,
            $"Committed expected-stdout.txt for '{name}' no longer matches gcc's live output. " +
            $"Re-run with {RunGccEnv}=1 {RegenBaselineEnv}=1 to refresh the baseline.");
    }

    /// <summary>
    /// Resolve the source-tree path to a fixture's <c>expected-stdout.txt</c>
    /// by walking up from <see cref="AppContext.BaseDirectory"/> to the
    /// directory holding <c>DotCC.FunctionalTests.csproj</c>, then joining
    /// <c>Fixtures/&lt;name&gt;/expected-stdout.txt</c>. Returns null when the
    /// project root can't be located. (Same logic as the MSVC oracle.)
    /// </summary>
    private static string? ResolveSourceSnapshotPath(string fixtureName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DotCC.FunctionalTests.csproj")))
            {
                return Path.Combine(dir.FullName, "Fixtures", fixtureName, "expected-stdout.txt");
            }
            dir = dir.Parent;
        }
        return null;
    }
}
