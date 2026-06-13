#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DotCC.FunctionalTests;

/// <summary>
/// Differential-testing oracle: compiles a C fixture with <c>gcc</c> and runs
/// the produced binary, so the test can assert that dotcc's emitted-C# output
/// matches a real C compiler byte-for-byte. The companion to
/// <see cref="MsvcOracle"/> — a second, independent reference compiler. Where
/// MSVC's C frontend lags the standard (e.g. it rejects the C23 bare
/// <c>bool</c> keyword even under <c>/std:clatest</c>), gcc tracks it, so this
/// oracle can cover ground cl.exe can't.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where gcc lives — two transports, one code path.</b> On a Linux/macOS
/// host (the CI <c>ubuntu-latest</c> / <c>ubuntu-24.04-arm</c> runners) gcc is
/// native, so we invoke <c>bash -lc "…"</c> directly. On Windows (the
/// maintainer's win-arm64 box) gcc isn't a native toolchain, but WSL ships a
/// real Linux gcc one <c>wsl.exe</c> hop away, so we shell out to
/// <c>wsl.exe bash -lc "…"</c> instead (mainline GNU C, not MinGW/Cygwin).
/// <see cref="RunShell"/> picks the transport; everything above it is shared.
/// This is what lets the gcc oracle run in CI on real Linux runners (x64 and
/// arm64) — gcc is dotcc's closest ABI twin (LP64), so it needs the fewest
/// per-fixture opt-outs.
/// </para>
/// <para>
/// <b>Path handling</b>: fixture sources are copied into an isolated
/// <c>workDir</c> (same as the MSVC oracle) and compiled/run there. On Linux
/// that directory is already a native path; on Windows it lives on the Windows
/// filesystem, so <see cref="ToShellPath"/> translates it to its
/// <c>/mnt/…</c> form via <c>wslpath -a</c>. Tiny fixtures make any
/// <c>/mnt</c> access cost irrelevant.
/// </para>
/// <para>
/// <b>Availability</b>: <see cref="IsAvailable"/> is false when no <c>gcc</c>
/// is reachable — no <c>gcc</c> on PATH on a Unix host, or (on Windows) no
/// <c>wsl.exe</c> / no <c>gcc</c> in the default distro. Tests skip rather than
/// fail in that case — same posture as the MSVC oracle.
/// </para>
/// </remarks>
internal static class GccWslOracle
{
    private static readonly object _initLock = new();
    private static bool _initialised;
    private static bool _available;
    private static string? _gccVersion;

    /// <summary>The <c>-std=</c> value gcc is invoked with. Matches dotcc's
    /// default dialect (c17, ISO — no GNU extensions, mirroring dotcc's
    /// no-<c>gnu*</c> stance), so the two agree on what's in the language.</summary>
    private const string DefaultStd = "c17";

    /// <summary>True if WSL is present and its default distro has gcc.</summary>
    public static bool IsAvailable
    {
        get { EnsureInitialised(); return _available; }
    }

    /// <summary>gcc version string (e.g. <c>13.3.0</c>), or null if unavailable.</summary>
    public static string? GccVersion
    {
        get { EnsureInitialised(); return _gccVersion; }
    }

    /// <summary>
    /// Compile <paramref name="csources"/> with gcc into a Linux binary and
    /// run it, returning captured stdout. Compilation + run happen in
    /// <paramref name="workDir"/> (created if missing). <paramref name="std"/>
    /// is the gcc <c>-std=</c> value (defaults to <see cref="DefaultStd"/>;
    /// the caller maps dotcc's dialect to gcc's spelling). Throws on gcc
    /// failure or non-zero program exit with the captured diagnostics.
    /// </summary>
    public static string CompileAndRun(string[] csources, string workDir, string std = DefaultStd, string[]? runArgs = null)
    {
        EnsureInitialised();
        if (!_available)
        {
            throw new InvalidOperationException("gcc/WSL oracle is not available on this host.");
        }
        Directory.CreateDirectory(workDir);

        // Copy each source into workDir so includes resolve, mirroring the
        // MSVC oracle. Also copy any sibling .h (so #include "foo.h" works) and
        // .bin (so C23 #embed "foo.bin" resolves) — both are name-relative to the
        // TU dir, the same way dotcc resolves them; .bin matches the fixture
        // convention + the DotCC.FunctionalTests.csproj embed-data copy glob.
        var localNames = new string[csources.Length];
        for (int i = 0; i < csources.Length; i++)
        {
            var name = Path.GetFileName(csources[i]);
            File.Copy(csources[i], Path.Combine(workDir, name), overwrite: true);
            localNames[i] = name;
        }
        foreach (var src in csources)
        {
            var dir = Path.GetDirectoryName(src);
            if (dir is null) { continue; }
            foreach (var pattern in new[] { "*.h", "*.bin" })
            {
                foreach (var aux in Directory.EnumerateFiles(dir, pattern))
                {
                    File.Copy(aux, Path.Combine(workDir, Path.GetFileName(aux)), overwrite: true);
                }
            }
        }

        var shellWorkDir = ToShellPath(workDir);
        const string binName = "gcc-oracle";

        // ── gcc compile ──  (-lm so the math fixtures link; harmless otherwise)
        var sourceList = string.Join(' ', localNames);
        // -lm for the math fixtures, -pthread for the <threads.h> fixtures
        // (harmless when unused; a no-op on glibc >= 2.34 where pthread folded
        // into libc, required on older glibc to link C11 threads).
        var compileCmd =
            $"cd '{shellWorkDir}' && gcc -std={std} {sourceList} -o {binName} -lm -pthread";
        var (compileOut, compileErr, compileExit) = RunShell(compileCmd);
        if (compileExit != 0)
        {
            throw new InvalidOperationException(
                $"gcc/WSL oracle: gcc failed (exit {compileExit}).\n--- gcc stdout ---\n{compileOut}\n--- gcc stderr ---\n{compileErr}");
        }

        // ── run produced binary ──
        var runCmd = $"cd '{shellWorkDir}' && ./{binName}";
        if (runArgs is not null)
        {
            foreach (var a in runArgs) { runCmd += " " + ShellQuote(a); }
        }
        var (runOut, runErr, runExit) = RunShell(runCmd);
        if (runExit != 0)
        {
            throw new InvalidOperationException(
                $"gcc/WSL oracle: produced binary failed (exit {runExit}).\n--- stdout ---\n{runOut}\n--- stderr ---\n{runErr}");
        }
        return runOut;
    }

    /// <summary>
    /// The working directory as the compile/run shell sees it. On a Unix host
    /// the shell is native, so the (forward-slashed) path is used as-is. On
    /// Windows the fixture lives on the Windows filesystem but gcc runs in WSL,
    /// so translate to the <c>/mnt/…</c> form via <c>wslpath -a</c> (forward-
    /// slash first — <c>wslpath</c> wants a clean absolute path; backslashes are
    /// ambiguous to the bridging layer).
    /// </summary>
    private static string ToShellPath(string workDir)
    {
        var fwd = workDir.Replace('\\', '/');
        if (!OperatingSystem.IsWindows()) { return fwd; }
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("wslpath");
        psi.ArgumentList.Add("-a");
        psi.ArgumentList.Add(fwd);
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to spawn wsl.exe for wslpath");
        var outp = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();
        if (proc.ExitCode != 0 || outp.Length == 0)
        {
            throw new InvalidOperationException($"wslpath failed for '{workDir}'.");
        }
        return outp;
    }

    /// <summary>
    /// Run a bash command line and return (stdout, stderr, exit). A login shell
    /// (<c>-l</c>) so PATH picks up gcc the way an interactive user would. The
    /// transport depends on the host: on Windows the reference gcc lives in WSL,
    /// so we hop through <c>wsl.exe bash -lc "&lt;cmd&gt;"</c>; on Linux/macOS gcc
    /// is native, so we invoke <c>/bin/bash -lc "&lt;cmd&gt;"</c> directly (no WSL,
    /// no path translation). Same command string either way.
    /// </summary>
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
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "wsl.exe";
            psi.ArgumentList.Add("bash");
        }
        else
        {
            psi.FileName = "/bin/bash";
        }
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to spawn the gcc oracle shell");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (stdout, stderr, proc.ExitCode);
    }

    /// <summary>Single-quote an argument for bash (wrap, and escape embedded quotes).</summary>
    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static void EnsureInitialised()
    {
        if (_initialised) { return; }
        lock (_initLock)
        {
            if (_initialised) { return; }
            _initialised = true;
            // Probe gcc through whichever transport this host uses (native bash
            // on Unix, wsl.exe on Windows). If the spawn or the probe fails —
            // no gcc on PATH, no wsl.exe, no distro gcc — leave _available
            // false so IsAvailable is false and the tests skip cleanly.
            try
            {
                var (outp, _, exit) = RunShell("gcc -dumpfullversion");
                if (exit == 0 && outp.Trim().Length > 0)
                {
                    _gccVersion = outp.Trim();
                    _available = true;
                }
            }
            catch
            {
                _available = false;
            }
        }
    }
}
