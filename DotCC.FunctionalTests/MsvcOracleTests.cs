#nullable enable

using System;
using System.IO;
using System.Linq;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// Opt-in differential test against MSVC's cl.exe. For each fixture,
/// compile + run the C source with MSVC and assert the output matches
/// the committed <c>expected-stdout.txt</c> baseline AND dotcc's emit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design — snapshot testing, not live differential</b>: the regular
/// <see cref="FixtureTests"/> already asserts dotcc's emit matches the
/// per-fixture <c>expected-stdout.txt</c>. That file IS the snapshot —
/// it was either authored to match MSVC's output or regenerated via
/// the regen mode below. Re-running cl.exe on every test invocation
/// to verify "MSVC still agrees" costs ~17s on the full suite and
/// gates the test run on Windows + a VS install. Skipping that work
/// by default keeps the dev cycle fast.
/// </para>
/// <para>
/// <b>Modes</b> (controlled by env vars):
/// <list type="bullet">
/// <item><b>Default</b> (no env var set): tests skip with a hint.
///   <see cref="FixtureTests"/> handles acceptance.</item>
/// <item><b><c>DOTCC_RUN_MSVC_ORACLE=1</c></b>: run cl.exe per fixture,
///   assert MSVC's output matches the committed
///   <c>expected-stdout.txt</c>. Catches real-world MSVC divergences
///   from the snapshot (e.g. cl.exe behavior changed between versions).
///   Slow — useful in CI on Windows agents.</item>
/// <item><b><c>DOTCC_REGEN_BASELINE=1</c></b>: same as run mode, but
///   when MSVC's output differs from the file, WRITE the new content
///   back to the source-tree <c>expected-stdout.txt</c> (resolved
///   from AppContext.BaseDirectory by walking up to the
///   <c>DotCC.FunctionalTests</c> project root). Use after intentional
///   behavior changes to refresh snapshots; review + commit the diff.</item>
/// </list>
/// </para>
/// <para>
/// When MSVC isn't available (non-Windows or no VS install) and the
/// MSVC mode was requested, tests skip with a clear message rather
/// than fail — keeps CI green where the oracle simply isn't reachable.
/// </para>
/// </remarks>
public sealed class MsvcOracleTests
{
    private const string RunMsvcEnv = "DOTCC_RUN_MSVC_ORACLE";
    private const string RegenBaselineEnv = "DOTCC_REGEN_BASELINE";

    private static bool RegenRequested =>
        Environment.GetEnvironmentVariable(RegenBaselineEnv) == "1";
    private static bool MsvcRunRequested =>
        RegenRequested || Environment.GetEnvironmentVariable(RunMsvcEnv) == "1";

    public static System.Collections.Generic.IEnumerable<object[]> Fixtures =>
        FixtureRunner.Discover().Select(f => new object[] { f.name, f.dir });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Dotcc_matches_msvc_output(string name, string dir)
    {
        if (!MsvcRunRequested)
        {
            Assert.Skip(
                $"MSVC oracle is opt-in. Set {RunMsvcEnv}=1 to verify cl.exe still " +
                $"matches the committed expected-stdout.txt, or {RegenBaselineEnv}=1 " +
                $"to refresh the baseline from MSVC. The fast FixtureTests already " +
                $"asserts dotcc's emit against expected-stdout.txt.");
        }
        if (!MsvcOracle.IsAvailable)
        {
            Assert.Skip(
                $"{(RegenRequested ? RegenBaselineEnv : RunMsvcEnv)} requested but " +
                $"MSVC oracle not available on this host (vswhere didn't find a VC install).");
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

        // MSVC path: copy sources to an isolated work dir, build with cl,
        // run, capture stdout.
        var workDir = Path.Combine(
            Path.GetTempPath(),
            $"dotcc-msvc-{name}-{Guid.NewGuid():N}");
        var msvcOut = MsvcOracle.CompileAndRun(match.sources, workDir)
            .ReplaceLineEndings("\n").TrimEnd('\n');

        // dotcc must match MSVC byte-for-byte. If this fails, dotcc's
        // emitter / libc has diverged from real-C semantics on this fixture.
        dotccOut.ShouldBe(msvcOut, $"dotcc emit diverges from MSVC on fixture '{name}'");

        // Snapshot check: the committed expected-stdout.txt should agree
        // with whatever MSVC just produced. If it doesn't, in regen mode
        // we rewrite the snapshot; otherwise we surface the drift so the
        // dev can investigate (maybe MSVC changed, maybe dotcc did).
        var snapshotRuntimePath = Path.Combine(dir, "expected-stdout.txt");
        var snapshot = File.ReadAllText(snapshotRuntimePath)
            .ReplaceLineEndings("\n").TrimEnd('\n');
        if (snapshot == msvcOut)
        {
            return; // snapshot already matches; nothing to do.
        }

        if (RegenRequested)
        {
            // Walk from the runtime fixture dir back to the source tree
            // and rewrite the on-disk snapshot. AppContext.BaseDirectory
            // is something like
            //   .../DotCC.FunctionalTests/bin/Release/net10.0/
            // and the runtime fixture dir is under that BaseDirectory's
            // Fixtures/. The matching source path lives at
            //   .../DotCC.FunctionalTests/Fixtures/<name>/expected-stdout.txt
            // which we get by walking up to the DotCC.FunctionalTests
            // project root and re-joining.
            var sourcePath = ResolveSourceSnapshotPath(name)
                ?? throw new InvalidOperationException(
                    $"Could not resolve source-tree expected-stdout.txt for fixture '{name}'.");
            File.WriteAllText(sourcePath, msvcOut + "\n");
            // Also keep the runtime copy in sync so subsequent test
            // invocations within this run see the new content.
            File.WriteAllText(snapshotRuntimePath, msvcOut + "\n");
            return;
        }

        // Not in regen mode and the snapshot drifted. Surface the
        // difference loudly so it gets reviewed rather than silently
        // updated. The user can re-run with DOTCC_REGEN_BASELINE=1 if
        // the MSVC output is the desired new snapshot.
        snapshot.ShouldBe(msvcOut,
            $"Committed expected-stdout.txt for '{name}' no longer matches MSVC's live output. " +
            $"Re-run with {RegenBaselineEnv}=1 to refresh the baseline.");
    }

    /// <summary>
    /// Resolve the source-tree path to a fixture's
    /// <c>expected-stdout.txt</c>. Walks up from
    /// <see cref="AppContext.BaseDirectory"/> until it finds a
    /// directory containing the <c>DotCC.FunctionalTests.csproj</c>
    /// (the project root), then joins
    /// <c>Fixtures/&lt;name&gt;/expected-stdout.txt</c>. Returns null
    /// when the project root can't be located (e.g. tests are running
    /// from a single-file deploy or unusual layout) — caller treats
    /// that as a fail-stop.
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
