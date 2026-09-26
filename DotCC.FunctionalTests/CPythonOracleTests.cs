#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// Opt-in differential oracle for dotcc's synthetic <c>&lt;Python.h&gt;</c> + the
/// abi3 shim (<c>DotCC.Libc/PythonLib.cs</c>): a fixture carrying a
/// <c>cpython-oracle.txt</c> sidecar (contents = the CPython version, e.g. <c>3.13</c>)
/// is ALSO built with <c>gcc</c> against the real CPython headers + <c>libpython</c>
/// (<c>python&lt;ver&gt;-config --embed</c>) with <c>Py_LIMITED_API</c> pinned to that
/// version, run, and its stdout compared with dotcc's and with the committed
/// <c>expected-stdout.txt</c>. So the same extension C source, driven by the same C
/// host, must print the same transcript — results AND exception messages — on real
/// CPython and on the shim.
/// </summary>
/// <remarks>
/// <para><b>Modes</b> (env vars): <c>DOTCC_RUN_CPYTHON_ORACLE=1</c> runs it (skips with a
/// hint otherwise); adding <c>DOTCC_REGEN_BASELINE=1</c> rewrites the snapshot from
/// CPython's output. Skips cleanly on a host without <c>bash</c>, <c>gcc</c> or the
/// matching <c>python&lt;ver&gt;-config</c> (Windows included — no WSL hop here).</para>
/// <para><b><c>Process.Start</c></b> is confined to this opt-in mode, as for every oracle.</para>
/// </remarks>
public sealed class CPythonOracleTests
{
    private const string RunEnv = "DOTCC_RUN_CPYTHON_ORACLE";
    private const string RegenBaselineEnv = "DOTCC_REGEN_BASELINE";
    private const string Marker = "cpython-oracle.txt";

    private static bool RunRequested => Environment.GetEnvironmentVariable(RunEnv) == "1";

    private static bool RegenRequested =>
        RunRequested && Environment.GetEnvironmentVariable(RegenBaselineEnv) == "1";

    public static TheoryData<string, string> Fixtures
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var f in FixtureRunner.Discover().Where(f => File.Exists(Path.Combine(f.dir, Marker))))
            {
                data.Add(f.name, f.dir);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Dotcc_matches_cpython_output(string name, string dir)
    {
        if (!RunRequested)
        {
            Assert.Skip($"CPython oracle is opt-in. Set {RunEnv}=1 to build '{name}' against real " +
                $"libpython and compare (add {RegenBaselineEnv}=1 to refresh expected-stdout.txt from it).");
        }
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("CPython oracle runs on Linux/macOS hosts only (gcc + python<ver>-config).");
        }
        var version = File.ReadAllText(Path.Combine(dir, Marker)).Trim();
        var parts = version.Split('.');
        var limitedApi = $"0x{int.Parse(parts[0]):X2}{int.Parse(parts[1]):X2}0000";
        var config = $"python{version}-config";
        if (Run("command -v gcc && command -v " + config, Path.GetTempPath()).exit != 0)
        {
            Assert.Skip($"{RunEnv} requested but gcc or {config} is not on PATH.");
        }

        var fixture = FixtureRunner.Discover().Single(f => f.name == name);
        var dotccOut = FixtureRunner.CompileAndRun(
                Compiler.EmitCSharp(fixture.sources, includeDirs: null, defines: null,
                    emit: EmitMode.Csproj, dialect: CDialect.Parse(fixture.std)),
                Array.Empty<string>())
            .ReplaceLineEndings("\n").TrimEnd('\n');

        var workDir = Path.Combine(Path.GetTempPath(), $"dotcc-cpython-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        foreach (var src in fixture.sources) { File.Copy(src, Path.Combine(workDir, Path.GetFileName(src))); }
        var build =
            $"gcc -std=c99 -DPy_LIMITED_API={limitedApi} $({config} --includes) *.c -o prog " +
            $"$({config} --embed --ldflags) 2>gcc.log || {{ cat gcc.log; exit 97; }}\n" +
            "./prog\n";
        var (cpyOut, exit) = Run(build, workDir);
        exit.ShouldBe(0, $"CPython build/run of '{name}' failed:\n{cpyOut}");
        cpyOut = cpyOut.ReplaceLineEndings("\n").TrimEnd('\n');

        dotccOut.ShouldBe(cpyOut, $"dotcc's Python shim diverges from CPython {version} on fixture '{name}'");

        var snapshotPath = Path.Combine(dir, "expected-stdout.txt");
        var snapshot = File.ReadAllText(snapshotPath).ReplaceLineEndings("\n").TrimEnd('\n');
        if (snapshot == cpyOut) { return; }
        if (RegenRequested)
        {
            var source = SourceSnapshotPath(name)
                ?? throw new InvalidOperationException($"Could not resolve the source-tree snapshot for '{name}'.");
            File.WriteAllText(source, cpyOut + "\n");
            File.WriteAllText(snapshotPath, cpyOut + "\n");
            return;
        }
        snapshot.ShouldBe(cpyOut,
            $"Committed expected-stdout.txt for '{name}' no longer matches CPython {version}. " +
            $"Re-run with {RunEnv}=1 {RegenBaselineEnv}=1 to refresh it.");
    }

    private static (string stdout, int exit) Run(string script, string workDir)
    {
        var psi = new ProcessStartInfo("bash")
        {
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start bash");
        p.StandardInput.Write("set -e\n" + script);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode == 0 ? stdout : stdout + stderr, p.ExitCode);
    }

    /// <summary>The fixture's committed snapshot in the source tree (walking up from the
    /// test binaries to the repo's <c>DotCC.FunctionalTests/Fixtures</c>).</summary>
    private static string? SourceSnapshotPath(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "DotCC.FunctionalTests", "Fixtures", name, "expected-stdout.txt");
            if (File.Exists(candidate)) { return candidate; }
        }
        return null;
    }
}
