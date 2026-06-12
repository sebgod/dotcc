#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace DotCC.FunctionalTests;

/// <summary>
/// Native shared-library round-trip oracle: compiles a C translation unit with
/// dotcc's <c>-shared</c> (library) mode, publishes it through NativeAOT to a real
/// <c>.so</c>/<c>.dll</c>, then compiles a hand-written C consumer that links the
/// native library and calls the exported (<c>[UnmanagedCallersOnly]</c>, cdecl)
/// functions — proving the export ABI works end-to-end, not just that the managed
/// metadata is shaped right (that's <see cref="LibraryModeTests"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in</b> (env <c>DOTCC_RUN_SHARED_LIB_ORACLE=1</c>), like the MSVC/gcc
/// oracles: NativeAOT publish is slow and needs a native toolchain (the ILC linker
/// = <c>clang</c> + zlib), and the consumer needs a C compiler. <see cref="IsAvailable"/>
/// is false (tests skip) unless <c>dotnet</c>, <c>gcc</c> and <c>clang</c> are all
/// reachable.
/// </para>
/// <para>
/// <b>Transport.</b> Everything below the in-process emit runs through a shell:
/// native <c>bash</c> on Linux/macOS (the CI ubuntu runners), or <c>wsl.exe bash</c>
/// on the maintainer's win-arm64 box (so the published artifact is a Linux <c>.so</c>
/// the WSL gcc links). The project + consumer are written to a host temp dir and
/// referenced via <see cref="ToShellPath"/> (a <c>/mnt/…</c> path on Windows, which
/// — unlike WSL's <c>/tmp</c> — survives across <c>wsl.exe</c> invocations). Publish +
/// link + run happen in a SINGLE shell script so the whole native dance is one session.
/// </para>
/// </remarks>
internal static class SharedLibOracle
{
    private static readonly object _initLock = new();
    private static bool _initialised;
    private static bool _available;
    private static string? _why;

    /// <summary>The library-mode assembly name → the native lib is
    /// <c>&lt;name&gt;.so</c> (NativeAOT emits no <c>lib</c> prefix, so the consumer
    /// links it by full path).</summary>
    private const string AsmName = "dotccsharedlib";

    /// <summary>True if dotnet + gcc + clang (the NativeAOT linker) are all reachable.</summary>
    public static bool IsAvailable { get { EnsureInitialised(); return _available; } }

    /// <summary>Why the oracle is unavailable (for the skip message), or null.</summary>
    public static string? Unavailable { get { EnsureInitialised(); return _why; } }

    /// <summary>
    /// Library-mode-compile <paramref name="libCSource"/> with dotcc, NativeAOT-publish
    /// it, then build + run <paramref name="consumerCSource"/> linked against the produced
    /// native library. Returns the consumer's stdout. Throws (test fails) on any
    /// publish/link/run error with the captured log.
    /// </summary>
    public static string PublishAndConsume(string libCSource, string consumerCSource)
    {
        EnsureInitialised();
        if (!_available)
        {
            throw new InvalidOperationException("shared-lib oracle is not available on this host.");
        }

        var workDir = Path.Combine(Path.GetTempPath(), "dotcc-sharedlib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        // ── in-process: emit the -shared program + its NativeAOT csproj ──
        var libC = Path.Combine(workDir, "lib.c");
        File.WriteAllText(libC, libCSource);
        var program = Compiler.EmitCSharp(
            new[] { libC }, includeDirs: null, defines: null, fileBased: false, libraryMode: true);
        File.WriteAllText(Path.Combine(workDir, "Program.cs"), program);
        File.WriteAllText(Path.Combine(workDir, AsmName + ".csproj"),
            Compiler.BuildGeneratedCsproj(libraryMode: true, assemblyName: AsmName));
        File.WriteAllText(Path.Combine(workDir, "consumer.c"), consumerCSource);

        // ── one shell script: publish → locate .so → link consumer → run ──
        var shellWork = ToShellPath(workDir);
        const string outMarker = "__CONSUMER_STDOUT__";
        var script =
            $"set -e; cd '{shellWork}'; " +
            "arch=$(uname -m); rid=linux-x64; [ \"$arch\" = aarch64 ] && rid=linux-arm64; " +
            "if ! dotnet publish -c Release -r $rid >publish.log 2>&1; then echo PUBLISH_FAILED; tail -25 publish.log; exit 3; fi; " +
            $"SO=$(find bin -name '{AsmName}.so' | head -1); " +
            "if [ -z \"$SO\" ]; then echo NO_SO; find bin -name '*.so'; exit 4; fi; " +
            "PUB=$(dirname \"$SO\"); " +
            "if ! gcc consumer.c -o consumer \"$SO\" -Wl,-rpath,\"$PUB\" 2>gcc.log; then echo GCC_FAILED; cat gcc.log; exit 5; fi; " +
            $"echo {outMarker}; ./consumer";

        var (stdout, stderr, exit) = RunShell(script);
        if (exit != 0 || !stdout.Contains(outMarker))
        {
            throw new InvalidOperationException(
                $"shared-lib oracle failed (exit {exit}).\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        }
        // Everything after the marker line is the consumer's own stdout.
        var idx = stdout.IndexOf(outMarker, StringComparison.Ordinal);
        var after = stdout[(idx + outMarker.Length)..];
        return after.TrimStart('\r', '\n');
    }

    /// <summary>Working directory as the shell sees it (forward-slashed; on Windows
    /// translated to its <c>/mnt/…</c> form via <c>wslpath -a</c> — persistent across
    /// <c>wsl.exe</c> calls, unlike WSL's <c>/tmp</c>).</summary>
    private static string ToShellPath(string dir)
    {
        var fwd = dir.Replace('\\', '/');
        if (!OperatingSystem.IsWindows()) { return fwd; }
        var psi = new ProcessStartInfo { FileName = "wsl.exe", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("wslpath");
        psi.ArgumentList.Add("-a");
        psi.ArgumentList.Add(fwd);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to spawn wsl.exe for wslpath");
        var outp = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();
        if (proc.ExitCode != 0 || outp.Length == 0) { throw new InvalidOperationException($"wslpath failed for '{dir}'."); }
        return outp;
    }

    /// <summary>Run a bash command line; returns (stdout, stderr, exit). Native bash
    /// on Unix, <c>wsl.exe bash</c> on Windows — same command either way.</summary>
    private static (string stdout, string stderr, int exit) RunShell(string command)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (OperatingSystem.IsWindows()) { psi.FileName = "wsl.exe"; psi.ArgumentList.Add("bash"); }
        else { psi.FileName = "/bin/bash"; }
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to spawn the shared-lib oracle shell");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (stdout, stderr, proc.ExitCode);
    }

    private static void EnsureInitialised()
    {
        if (_initialised) { return; }
        lock (_initLock)
        {
            if (_initialised) { return; }
            _initialised = true;
            try
            {
                // Need dotnet (publish), gcc (consumer), clang (NativeAOT linker).
                var (outp, _, exit) = RunShell(
                    "for t in dotnet gcc clang; do command -v $t >/dev/null 2>&1 || { echo MISSING:$t; }; done; echo PROBE_DONE");
                if (exit != 0 || !outp.Contains("PROBE_DONE"))
                {
                    _why = "no bash/WSL shell reachable";
                    return;
                }
                if (outp.Contains("MISSING:"))
                {
                    var missing = string.Join(", ",
                        outp.Split('\n')
                            .Where(l => l.StartsWith("MISSING:", StringComparison.Ordinal))
                            .Select(l => l.Trim()["MISSING:".Length..]));
                    _why = $"NativeAOT/consumer toolchain not found: {missing}";
                    return;
                }
                _available = true;
            }
            catch (Exception ex)
            {
                _why = "shell probe failed: " + ex.Message;
                _available = false;
            }
        }
    }
}
