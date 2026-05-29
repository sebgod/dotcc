#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DotCC.FunctionalTests;

/// <summary>
/// Differential-testing oracle: compiles a C fixture with <c>gcc</c> inside
/// WSL and runs the resulting Linux binary, so the test can assert that
/// dotcc's emitted-C# output matches a real C compiler byte-for-byte. The
/// companion to <see cref="MsvcOracle"/> — a second, independent reference
/// compiler. Where MSVC's C frontend lags the standard (e.g. it rejects the
/// C23 bare <c>bool</c> keyword even under <c>/std:clatest</c>), gcc tracks
/// it, so this oracle can cover ground cl.exe can't.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why WSL</b>: the primary dev/CI host here is Windows (win-arm64). gcc
/// isn't a native Windows toolchain, but WSL ships a real Linux gcc one
/// <c>wsl.exe</c> hop away. We shell out to <c>wsl.exe bash -lc "…"</c> rather
/// than depend on MinGW/Cygwin so the reference really is mainline GNU C.
/// </para>
/// <para>
/// <b>Path bridging</b>: fixture sources live on the Windows filesystem. We
/// copy them into an isolated Windows <c>workDir</c> (same as the MSVC
/// oracle), then translate that directory to its <c>/mnt/…</c> form via
/// <c>wslpath -a</c> and compile/run there. Tiny fixtures make the
/// <c>/mnt</c> access cost irrelevant.
/// </para>
/// <para>
/// <b>Availability</b>: <see cref="IsAvailable"/> is false on non-Windows
/// hosts, when <c>wsl.exe</c> is absent, or when the default distro has no
/// <c>gcc</c>. Tests skip rather than fail in that case — same posture as the
/// MSVC oracle.
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
        // MSVC oracle. Also copy any sibling .h so #include "foo.h" works.
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
            foreach (var hpath in Directory.EnumerateFiles(dir, "*.h"))
            {
                File.Copy(hpath, Path.Combine(workDir, Path.GetFileName(hpath)), overwrite: true);
            }
        }

        var wslWorkDir = ToWslPath(workDir);
        const string binName = "gcc-oracle";

        // ── gcc compile ──  (-lm so the math fixtures link; harmless otherwise)
        var sourceList = string.Join(' ', localNames);
        // -lm for the math fixtures, -pthread for the <threads.h> fixtures
        // (harmless when unused; a no-op on glibc >= 2.34 where pthread folded
        // into libc, required on older glibc to link C11 threads).
        var compileCmd =
            $"cd '{wslWorkDir}' && gcc -std={std} {sourceList} -o {binName} -lm -pthread";
        var (compileOut, compileErr, compileExit) = RunWsl(compileCmd);
        if (compileExit != 0)
        {
            throw new InvalidOperationException(
                $"gcc/WSL oracle: gcc failed (exit {compileExit}).\n--- gcc stdout ---\n{compileOut}\n--- gcc stderr ---\n{compileErr}");
        }

        // ── run produced binary ──
        var runCmd = $"cd '{wslWorkDir}' && ./{binName}";
        if (runArgs is not null)
        {
            foreach (var a in runArgs) { runCmd += " " + ShellQuote(a); }
        }
        var (runOut, runErr, runExit) = RunWsl(runCmd);
        if (runExit != 0)
        {
            throw new InvalidOperationException(
                $"gcc/WSL oracle: produced binary failed (exit {runExit}).\n--- stdout ---\n{runOut}\n--- stderr ---\n{runErr}");
        }
        return runOut;
    }

    /// <summary>
    /// Translate a Windows path to its WSL <c>/mnt/…</c> form via
    /// <c>wslpath -a</c>. Forward-slash the input first — <c>wslpath</c> wants
    /// a clean absolute path and backslashes are ambiguous to the bridging
    /// layer.
    /// </summary>
    private static string ToWslPath(string windowsPath)
    {
        var fwd = windowsPath.Replace('\\', '/');
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
            throw new InvalidOperationException($"wslpath failed for '{windowsPath}'.");
        }
        return outp;
    }

    /// <summary>
    /// Run a bash command line in the WSL default distro via
    /// <c>wsl.exe bash -lc "&lt;cmd&gt;"</c> and return (stdout, stderr, exit).
    /// A login shell (<c>-l</c>) so the distro's PATH picks up gcc the same way
    /// an interactive user would.
    /// </summary>
    private static (string stdout, string stderr, int exit) RunWsl(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to spawn wsl.exe");
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
            // wsl.exe is a Windows host concept. Skip on other platforms.
            if (!OperatingSystem.IsWindows()) { return; }
            try
            {
                var (outp, _, exit) = RunWsl("gcc -dumpfullversion");
                if (exit == 0 && outp.Trim().Length > 0)
                {
                    _gccVersion = outp.Trim();
                    _available = true;
                }
            }
            catch
            {
                // Leave _available false — IsAvailable returns false and tests skip.
                _available = false;
            }
        }
    }
}
