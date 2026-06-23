#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace DotCC.FunctionalTests;

/// <summary>
/// Differential-testing oracle for the Zig front-end: compiles a <c>.zig</c>
/// source with the real <c>zig</c> compiler and runs the produced binary, so a
/// test can assert dotcc's Zig path (parse → <c>ZigLowering</c> → IR → C#
/// backend → run) agrees with upstream Zig on the same program. The Zig analogue
/// of <see cref="GccWslOracle"/> / <see cref="MsvcOracle"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One transport, every host — no WSL hop.</b> Unlike the gcc oracle (gcc
/// isn't a native Windows toolchain, so on the maintainer's win-arm64 box it
/// reaches a real gcc one <c>wsl.exe</c> hop away), <c>zig</c> ships as a single
/// self-contained binary on every platform — native on the Windows dev box AND
/// on the Linux CI runner. So we invoke <c>zig</c> directly through
/// <see cref="ProcessStartInfo.ArgumentList"/>: no bash, no <c>wsl.exe</c>, no
/// <c>/mnt/…</c> path translation. That makes this the simplest of the three
/// oracles.
/// </para>
/// <para>
/// <b>Compile then run</b> (mirrors the gcc oracle): the root <c>.zig</c> (plus
/// any sibling <c>.zig</c> it <c>@import</c>s, and — once <c>@cImport</c> lands —
/// any sibling <c>.h</c>) is copied into an isolated <c>workDir</c> and built
/// there with <c>zig build-exe</c> (a non-zero exit is a real compile error →
/// throw with diagnostics). The produced binary is then run <i>separately</i>,
/// so its own stdout + exit code are what we capture — not conflated with zig's
/// own exit the way <c>zig run</c> would.
/// </para>
/// <para>
/// <b>Version.</b> dotcc's Zig grammar is pinned to the langref at zig source
/// <c>3391ad7a</c> = <c>0.17.0-dev.667+0569f1f6a</c>; the CI install step pins
/// that build and asserts <c>zig version</c> (no silent skip). Locally the
/// oracle just uses whatever <c>zig</c> is on PATH and records its version.
/// </para>
/// <para>
/// <b>Availability.</b> <see cref="IsAvailable"/> is false when no <c>zig</c> is
/// on PATH; tests skip rather than fail — same posture as the gcc/MSVC oracles.
/// </para>
/// </remarks>
internal static class ZigOracle
{
    private static readonly object _initLock = new();
    private static bool _initialised;
    private static bool _available;
    private static string? _zigVersion;

    /// <summary>True if a <c>zig</c> compiler is reachable on PATH.</summary>
    public static bool IsAvailable
    {
        get { EnsureInitialised(); return _available; }
    }

    /// <summary>zig version string (e.g. <c>0.17.0-dev.667+0569f1f6a</c>), or null if unavailable.</summary>
    public static string? ZigVersion
    {
        get { EnsureInitialised(); return _zigVersion; }
    }

    /// <summary>
    /// Build <paramref name="rootZig"/> with <c>zig build-exe</c> into
    /// <paramref name="workDir"/> (created if missing) and run the produced
    /// binary, returning its captured stdout and exit code. Sibling
    /// <c>.zig</c>/<c>.h</c> files are copied alongside so <c>@import</c> /
    /// <c>@cImport</c> resolve name-relative to the root, the way dotcc resolves
    /// them. Throws on a zig <i>compile</i> failure (with the captured
    /// diagnostics); a non-zero <i>program</i> exit is returned, not thrown —
    /// for a Zig program the exit code is itself an observable the differential
    /// compares (unlike the gcc oracle's C fixtures, which all exit 0).
    /// </summary>
    public static (string stdout, int exit) CompileAndRun(string rootZig, string workDir, string[]? runArgs = null)
    {
        EnsureInitialised();
        if (!_available)
        {
            throw new InvalidOperationException("zig oracle is not available on this host (no `zig` on PATH).");
        }
        Directory.CreateDirectory(workDir);

        // Copy the root and any sibling .zig/.h — imports resolve name-relative
        // to the root's directory, the same convention dotcc uses. CopyInto skips
        // a self-copy, so a root that already lives in workDir is fine.
        var rootName = Path.GetFileName(rootZig);
        CopyInto(rootZig, workDir);
        var srcDir = Path.GetDirectoryName(rootZig);
        if (srcDir is not null)
        {
            // `*.c` is copied too (Milestone V): a MIXED .c + .zig program is built by
            // listing its C translation units alongside the root .zig — exactly the input
            // set dotcc's `EmitCSharp` receives. zig ships its own C compiler, so no
            // external cc is needed.
            foreach (var pattern in new[] { "*.zig", "*.h", "*.c" })
            {
                foreach (var aux in Directory.EnumerateFiles(srcDir, pattern))
                {
                    CopyInto(aux, workDir);
                }
            }
        }

        var binName = OperatingSystem.IsWindows() ? "zig-oracle.exe" : "zig-oracle";

        // ── zig compile ──  (-lc links libc so `extern fn` FFI — printf/putchar/… —
        // resolves; harmless for a pure-Zig program, which simply doesn't use it.) Any
        // sibling .c translation units (Milestone V — mixed C↔Zig interop) are listed as
        // additional positional sources, so `std.heap.c_allocator` memory and C
        // `malloc`/`free` share one heap across the seam in the produced binary too.
        var cSources = Directory.EnumerateFiles(workDir, "*.c").Select(p => Path.GetFileName(p)!).ToArray();
        var buildArgs = new List<string> { "build-exe", rootName };
        buildArgs.AddRange(cSources);
        buildArgs.Add("-lc");
        buildArgs.Add("-femit-bin=" + binName);
        var (cOut, cErr, cExit) = RunProcess("zig", workDir, buildArgs.ToArray());
        if (cExit != 0)
        {
            throw new InvalidOperationException(
                $"zig oracle: `zig build-exe` failed (exit {cExit}).\n--- zig stdout ---\n{cOut}\n--- zig stderr ---\n{cErr}");
        }

        // ── run produced binary ──
        var binPath = Path.Combine(workDir, binName);
        var (rOut, _, rExit) = RunProcess(binPath, workDir, runArgs ?? Array.Empty<string>());
        return (rOut, rExit);
    }

    /// <summary>Copy <paramref name="src"/> into <paramref name="destDir"/> under
    /// its own name, skipping the no-op self-copy when it already lives there.</summary>
    private static void CopyInto(string src, string destDir)
    {
        var dst = Path.Combine(destDir, Path.GetFileName(src));
        if (Path.GetFullPath(src) == Path.GetFullPath(dst)) { return; }
        File.Copy(src, dst, overwrite: true);
    }

    /// <summary>
    /// Run <paramref name="fileName"/> with <paramref name="args"/> in
    /// <paramref name="workDir"/> and return (stdout, stderr, exit). Direct
    /// process spawn — no shell, since <c>zig</c> and the binary it produces are
    /// both native executables on every host.
    /// </summary>
    private static (string stdout, string stderr, int exit) RunProcess(string fileName, string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) { psi.ArgumentList.Add(a); }
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to spawn '{fileName}'");
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
            // Probe `zig version`. If the spawn or probe fails — no zig on PATH —
            // leave _available false so IsAvailable is false and tests skip cleanly.
            try
            {
                var (outp, _, exit) = RunProcess("zig", Environment.CurrentDirectory, "version");
                if (exit == 0 && outp.Trim().Length > 0)
                {
                    _zigVersion = outp.Trim();
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
