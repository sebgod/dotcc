#nullable enable

using System;
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
/// Compilation goes through a generated wrapper batch that
/// <c>call vcvars64.bat</c> first, then <c>cl /Fe:&lt;exe&gt; ...</c>;
/// vcvars sets up <c>PATH</c>, <c>INCLUDE</c>, <c>LIB</c> in the cmd
/// session so cl can find headers and link against MSVCRT. Output of
/// vcvars is captured and discarded — we only care about the program's
/// stdout after it runs.
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

    /// <summary>True if vswhere found a VS install with VC tools.</summary>
    public static bool IsAvailable
    {
        get { EnsureInitialised(); return _vcvarsBatPath is not null; }
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
    /// </summary>
    public static string CompileAndRun(string[] csources, string workDir, string[]? runArgs = null)
    {
        EnsureInitialised();
        if (_vcvarsBatPath is null)
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

        var exeName = "msvc-oracle.exe";
        var batPath = Path.Combine(workDir, "msvc-build-and-run.bat");
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine($"cd /d \"{workDir}\"");
        // Quote the vcvars path — VS lives under Program Files.
        sb.AppendLine($"call \"{_vcvarsBatPath}\" >NUL 2>NUL");
        sb.Append("cl /nologo /Fe:").Append(exeName);
        foreach (var n in localNames) { sb.Append(' ').Append(n); }
        sb.AppendLine(" 1>cl.stdout.log 2>cl.stderr.log");
        sb.AppendLine("if errorlevel 1 ( type cl.stdout.log & type cl.stderr.log & exit /b 1 )");
        sb.Append($".\\{exeName}");
        if (runArgs is not null)
        {
            foreach (var a in runArgs) { sb.Append(' ').Append('"').Append(a).Append('"'); }
        }
        sb.AppendLine();
        File.WriteAllText(batPath, sb.ToString());

        // Note: vcvars64.bat *must* run without output redirection from the
        // .bat-level `call`; redirecting `>NUL` at the inner level (as the
        // bat does) works, but redirecting the outer `cmd /c` would suppress
        // env propagation. We use Process.Start with redirected stdout/err
        // on the cmd.exe wrapper — that's at the parent level, doesn't
        // interfere with vcvars' own internal redirects.
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
        };
        psi.ArgumentList.Add("/C");
        psi.ArgumentList.Add(batPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to spawn cmd.exe for MSVC oracle");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"MSVC oracle build/run failed (exit {proc.ExitCode}).\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        }
        return stdout;
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
            try { ProbeVswhere(); }
            catch { /* leave _vcvarsBatPath null — IsAvailable returns false */ }
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
}
