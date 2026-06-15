#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// Opt-in differential test for the Zig front-end: compile + run a Zig program
/// through dotcc (emit C# → Roslyn → run) AND through the real <c>zig</c>
/// compiler, then assert they agree. The Zig analogue of
/// <see cref="GccWslOracleTests"/> / <see cref="MsvcOracleTests"/>, but a PURE
/// differential — there is no committed snapshot to validate. The always-on
/// <see cref="ZigFrontendTests"/> already pins dotcc's emit; here real zig IS
/// the oracle, so the two pipelines are compared head-to-head with no baseline
/// file in between.
/// </summary>
/// <remarks>
/// <para>
/// <b>Skeleton — grows at <c>@cImport</c>.</b> The current Zig surface has no
/// I/O, so the only observable is the <b>process exit code</b> (the program's
/// <c>main</c> return) — which is why this compares exit codes and
/// <see cref="FixtureRunner.CompileAndRunCapturingExit"/> exists. Once
/// <c>@cImport</c> brings <c>c.printf</c> → stdout, this gains (a) a fixture-dir
/// convention — a <c>ZigFixtures/&lt;name&gt;/</c> walk mirroring
/// <see cref="FixtureRunner.Discover"/>, one <c>[Theory]</c> case per directory —
/// and (b) a stdout differential alongside the exit-code one. For now it is a
/// single inline smoke test that proves the whole wiring end-to-end: toolchain
/// present, both pipelines run, exit codes agree.
/// </para>
/// <para>
/// <b>Modes</b> (env vars): opt-in via <c>DOTCC_RUN_ZIG_ORACLE=1</c> (skips with
/// a hint otherwise); skips with a clear message when no <c>zig</c> is on PATH.
/// Same posture as the gcc/MSVC oracles — the toolchain absence is a skip, not a
/// failure.
/// </para>
/// </remarks>
public sealed class ZigOracleTests
{
    private const string RunZigEnv = "DOTCC_RUN_ZIG_ORACLE";

    private static bool ZigRunRequested =>
        Environment.GetEnvironmentVariable(RunZigEnv) == "1";

    [Fact]
    public void Dotcc_matches_zig_exit_code()
    {
        if (!ZigRunRequested)
        {
            Assert.Skip(
                $"Zig oracle is opt-in. Set {RunZigEnv}=1 to compile + run the program " +
                $"with the real zig compiler and assert dotcc's Zig path agrees. The " +
                $"always-on ZigFrontendTests already pins dotcc's emit.");
        }
        if (!ZigOracle.IsAvailable)
        {
            Assert.Skip($"{RunZigEnv} requested but no `zig` is on PATH on this host.");
        }

        // The vertical-slice program: yields 42 via the process exit code (Zig's
        // `pub fn main() u8` returns the exit code), no I/O.
        const string program = "pub fn main() u8 {\n    const x: u8 = 40;\n    return x + 2;\n}\n";

        // dotcc path: write a temp .zig, emit C# (csproj-shaped — Roslyn rejects
        // the #:property header), compile in-process, run, capture the exit code.
        var zigPath = Path.Combine(Path.GetTempPath(), $"dotcc-zig-oracle-{Guid.NewGuid():N}.zig");
        File.WriteAllText(zigPath, program);
        int dotccExit;
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { zigPath }, fileBased: false);
            (_, dotccExit) = FixtureRunner.CompileAndRunCapturingExit(emitted, Array.Empty<string>());
        }
        finally { File.Delete(zigPath); }

        // zig path: build + run the SAME source with the real compiler.
        var workDir = Path.Combine(Path.GetTempPath(), $"dotcc-zig-oracle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var zigSrc = Path.Combine(workDir, "main.zig");
        File.WriteAllText(zigSrc, program);
        try
        {
            var (zigStdout, zigExit) = ZigOracle.CompileAndRun(zigSrc, workDir);

            // No I/O yet → stdout empty on both sides; the exit code is the
            // observable the differential turns on.
            dotccExit.ShouldBe(zigExit, "dotcc's Zig path diverges from real zig on the exit code");
            dotccExit.ShouldBe(42);
            zigStdout.ShouldBeEmpty();
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
