#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DotCC.FunctionalTests;

/// <summary>
/// Differential-testing oracle: compiles a C fixture with MSVC's cl.exe and
/// runs the resulting native binary so the test can assert that dotcc's
/// emitted-C# output matches a real C compiler byte-for-byte.
/// </summary>
/// <remarks>
/// Discovery uses Microsoft's <c>vswhere.exe</c>, which is the only
/// supported way to locate a Visual Studio install on modern Windows
/// (registry queries are unreliable since VS 2017 went side-by-side).
///
/// <para>
/// <b>vcvars caching</b>: <c>vcvars64.bat</c> sets up <c>PATH</c>,
/// <c>INCLUDE</c>, <c>LIB</c>, etc. for cl.exe. The naive approach is
/// "wrap every test in a <c>cmd /c call vcvars && cl …</c>" — but
/// spawning cmd.exe + running the bat per test costs ~1s of startup
/// overhead per fixture. Instead we run vcvars ONCE at
/// <see cref="EnsureInitialised"/> time, capture the resulting
/// environment, and spawn cl.exe directly with that env per test. Cuts
/// per-test wall time by ~5x for the oracle pass.
/// </para>
///
/// On non-Windows or hosts without VS, <see cref="IsAvailable"/> is false
/// and tests should <see cref="Xunit.Assert.Skip"/> to keep CI green where
/// the oracle simply isn't reachable.
/// </remarks>
internal static class MsvcOracle
{
    private static readonly object _initLock = new();
    private static bool _initialised;
    private static string? _vsInstallPath;
    private static string? _vcvarsBatPath;
    // Captured environment after `call vcvars64.bat`. Each ProcessStartInfo's
    // Environment is populated from this so cl.exe finds its headers / libs
    // without needing to re-invoke the bat per test.
    private static Dictionary<string, string>? _vcvarsEnv;
    private static string? _clExePath;

    /// <summary>True if vswhere found a VS install with VC tools.</summary>
    public static bool IsAvailable
    {
        get { EnsureInitialised(); return _clExePath is not null; }
    }

    /// <summary>Discovered VS install root (e.g. <c>C:\Program Files\Microsoft Visual Studio\18\Professional</c>).</summary>
    public static string? VsInstallPath
    {
        get { EnsureInitialised(); return _vsInstallPath; }
    }

    /// <summary>
    /// Compile <paramref name="csources"/> into a native exe and run it,
    /// returning captured stdout. Compilation + run happen in
    /// <paramref name="workDir"/> (created if missing). Throws on cl
    /// failure with the captured diagnostics in the message.
    /// <paramref name="stdFlag"/> is the cl language-mode switch (e.g.
    /// <c>/std:c11</c>) or null for cl's default mode — required because
    /// cl's default C mode is legacy C89+extensions, where C11 syntax like
    /// <c>_Generic</c> is a hard syntax error (C2059).
    /// </summary>
    public static string CompileAndRun(string[] csources, string workDir, string? stdFlag = null, string[]? runArgs = null)
    {
        EnsureInitialised();
        if (_clExePath is null || _vcvarsEnv is null)
        {
            throw new InvalidOperationException("MSVC oracle is not available on this host.");
        }
        Directory.CreateDirectory(workDir);

        // Copy each source into workDir so includes resolve. Real-world C
        // projects use -I but this is enough for our fixtures.
        var localNames = new string[csources.Length];
        for (int i = 0; i < csources.Length; i++)
        {
            var name = Path.GetFileName(csources[i]);
            File.Copy(csources[i], Path.Combine(workDir, name), overwrite: true);
            localNames[i] = name;
        }
        // Also copy any .h alongside the .c sources so #include "foo.h" works.
        foreach (var src in csources)
        {
            var dir = Path.GetDirectoryName(src);
            if (dir is null) { continue; }
            foreach (var hpath in Directory.EnumerateFiles(dir, "*.h"))
            {
                File.Copy(hpath, Path.Combine(workDir, Path.GetFileName(hpath)), overwrite: true);
            }
        }

        // ── cl.exe compile ──
        var exeName = "msvc-oracle.exe";
        var cl = new ProcessStartInfo
        {
            FileName = _clExePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
        };
        ApplyCachedEnv(cl);
        cl.ArgumentList.Add("/nologo");
        if (stdFlag is not null) { cl.ArgumentList.Add(stdFlag); }
        cl.ArgumentList.Add($"/Fe:{exeName}");
        foreach (var n in localNames) { cl.ArgumentList.Add(n); }

        using (var clProc = Process.Start(cl)
            ?? throw new InvalidOperationException("failed to spawn cl.exe for MSVC oracle"))
        {
            var clOut = clProc.StandardOutput.ReadToEnd();
            var clErr = clProc.StandardError.ReadToEnd();
            clProc.WaitForExit();
            if (clProc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"MSVC oracle: cl.exe failed (exit {clProc.ExitCode}).\n--- cl stdout ---\n{clOut}\n--- cl stderr ---\n{clErr}");
            }
        }

        // ── run produced .exe ──
        var run = new ProcessStartInfo
        {
            FileName = Path.Combine(workDir, exeName),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
        };
        ApplyCachedEnv(run);
        if (runArgs is not null)
        {
            foreach (var a in runArgs) { run.ArgumentList.Add(a); }
        }

        using var runProc = Process.Start(run)
            ?? throw new InvalidOperationException("failed to spawn MSVC-built exe");
        var stdout = runProc.StandardOutput.ReadToEnd();
        var stderr = runProc.StandardError.ReadToEnd();
        runProc.WaitForExit();
        if (runProc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"MSVC oracle: produced exe failed (exit {runProc.ExitCode}).\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        }
        return stdout;
    }

    /// <summary>
    /// Apply the cached vcvars environment to <paramref name="psi"/>'s
    /// Environment dict. We replace rather than merge — Process.Start
    /// already seeds Environment from the parent before we touch it, so
    /// just overlay the vcvars-set keys on top.
    /// </summary>
    private static void ApplyCachedEnv(ProcessStartInfo psi)
    {
        foreach (var (k, v) in _vcvarsEnv!) { psi.Environment[k] = v; }
    }

    private static void EnsureInitialised()
    {
        if (_initialised) { return; }
        lock (_initLock)
        {
            if (_initialised) { return; }
            _initialised = true;
            // Windows-only oracle. Skip silently on other platforms.
            if (!OperatingSystem.IsWindows()) { return; }
            try
            {
                ProbeVswhere();
                if (_vcvarsBatPath is not null)
                {
                    CaptureVcvarsEnv();
                    LocateClExe();
                }
            }
            catch
            {
                // Leave fields null — IsAvailable returns false and tests skip.
                _vcvarsEnv = null;
                _clExePath = null;
            }
        }
    }

    private static void ProbeVswhere()
    {
        var pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (string.IsNullOrEmpty(pf86)) { return; }
        var vswhere = Path.Combine(pf86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) { return; }

        var psi = new ProcessStartInfo
        {
            FileName = vswhere,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-latest");
        psi.ArgumentList.Add("-products");
        psi.ArgumentList.Add("*");
        psi.ArgumentList.Add("-requires");
        psi.ArgumentList.Add("Microsoft.VisualStudio.Component.VC.Tools.x86.x64");
        psi.ArgumentList.Add("-property");
        psi.ArgumentList.Add("installationPath");
        using var proc = Process.Start(psi);
        if (proc is null) { return; }
        var path = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();
        if (proc.ExitCode != 0 || string.IsNullOrEmpty(path)) { return; }

        var vcvars = Path.Combine(path, "VC", "Auxiliary", "Build", "vcvars64.bat");
        if (!File.Exists(vcvars)) { return; }
        _vsInstallPath = path;
        _vcvarsBatPath = vcvars;
    }

    /// <summary>
    /// One-shot vcvars64.bat invocation: run it through cmd.exe, then
    /// dump <c>set</c> to capture the post-vcvars environment. Parse the
    /// <c>KEY=VALUE</c> lines into a dictionary that subsequent process
    /// launches splice into their <see cref="ProcessStartInfo.Environment"/>.
    /// </summary>
    private static void CaptureVcvarsEnv()
    {
        // Use raw `Arguments` rather than ArgumentList — ArgumentList
        // escapes shell metacharacters like `&&`, `>`, and quoted paths
        // in a way that confuses cmd.exe. The command is built so
        // vcvars's stdout/stderr go to NUL (avoiding the "Setting up..."
        // banner mixing with the env dump), then `set` writes KEY=VALUE
        // lines to OUR captured stdout.
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{_vcvarsBatPath}\" >NUL 2>NUL && set\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to spawn cmd.exe for vcvars capture");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"vcvars capture failed (exit {proc.ExitCode}).\n--- stdout (first 500) ---\n{stdout[..Math.Min(500, stdout.Length)]}\n--- stderr ---\n{stderr}");
        }
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) { continue; }
            env[trimmed[..eq]] = trimmed[(eq + 1)..];
        }
        _vcvarsEnv = env;
    }

    /// <summary>
    /// Look up <c>cl.exe</c> on the vcvars-modified <c>PATH</c>. Stored
    /// as an absolute path so each <see cref="CompileAndRun"/> call can
    /// invoke it without re-resolving.
    /// </summary>
    private static void LocateClExe()
    {
        if (_vcvarsEnv is null) { return; }
        if (!_vcvarsEnv.TryGetValue("PATH", out var path)) { return; }
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) { continue; }
            var candidate = Path.Combine(dir, "cl.exe");
            if (File.Exists(candidate))
            {
                _clExePath = candidate;
                return;
            }
        }
    }
}
