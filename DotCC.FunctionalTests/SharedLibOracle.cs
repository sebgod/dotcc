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

    /// <summary>Assembly name of the dotcc-emitted dlopen consumer (a plain managed
    /// exe) in the dotcc-consumes-dotcc round-trip.</summary>
    private const string ConsumerAsmName = "dotccdlconsumer";

    /// <summary>Assembly name of the dotcc-emitted IMPORT-mode consumer (a managed exe
    /// that binds the native lib's exports via <c>-l</c>, no <c>dlopen</c>).</summary>
    private const string ImportConsumerAsmName = "dotccimportconsumer";

    private const string OutMarker = "__CONSUMER_STDOUT__";

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
        var script =
            "set -e\n" +
            "cd \"$(dirname \"$0\")\"\n" +
            "arch=$(uname -m); rid=linux-x64; [ \"$arch\" = aarch64 ] && rid=linux-arm64\n" +
            "if ! dotnet publish -c Release -r $rid >publish.log 2>&1; then echo PUBLISH_FAILED; tail -25 publish.log; exit 3; fi\n" +
            $"SO=$(find bin -name '{AsmName}.so' | head -1)\n" +
            "if [ -z \"$SO\" ]; then echo NO_SO; find bin -name '*.so'; exit 4; fi\n" +
            "PUB=$(dirname \"$SO\")\n" +
            "if ! gcc consumer.c -o consumer \"$SO\" -Wl,-rpath,\"$PUB\" 2>gcc.log; then echo GCC_FAILED; cat gcc.log; exit 5; fi\n" +
            $"echo {OutMarker}\n./consumer\n";

        return RunCapture(workDir, script);
    }

    /// <summary>
    /// The dotcc-consumes-dotcc round-trip: library-mode-compile
    /// <paramref name="libCSource"/> and NativeAOT-publish it to a native
    /// <c>.so</c> (as above), then compile <paramref name="dotccConsumerCSource"/>
    /// — a C program that <c>dlopen</c>s the <c>.so</c> and calls its exports via
    /// <c>dlsym</c> — with dotcc to a managed exe, and run it passing the <c>.so</c>
    /// path as <c>argv[1]</c>. Returns the consumer's stdout. This closes the loop
    /// the gcc consumer leaves open: a dotcc-built program consuming a dotcc-built
    /// native library, with dotcc's <c>delegate* unmanaged[Cdecl]</c> dlsym call
    /// sites meeting the <c>[UnmanagedCallersOnly]</c> cdecl exports — convention
    /// symmetry proven end-to-end.
    /// </summary>
    /// <remarks>lib and consumer live in sibling subdirectories so neither csproj's
    /// recursive <c>**/*.cs</c> glob swallows the other's <c>Program.cs</c>.</remarks>
    public static string PublishAndConsumeViaDotcc(string libCSource, string dotccConsumerCSource)
    {
        EnsureInitialised();
        if (!_available)
        {
            throw new InvalidOperationException("shared-lib oracle is not available on this host.");
        }

        var workDir = Path.Combine(Path.GetTempPath(), "dotcc-sharedlib-dl-" + Guid.NewGuid().ToString("N"));
        var libDir = Path.Combine(workDir, "lib");
        var consDir = Path.Combine(workDir, "consumer");
        Directory.CreateDirectory(libDir);
        Directory.CreateDirectory(consDir);

        // ── in-process: the -shared lib project (NativeAOT) ──
        var libC = Path.Combine(libDir, "lib.c");
        File.WriteAllText(libC, libCSource);
        File.WriteAllText(Path.Combine(libDir, "Program.cs"),
            Compiler.EmitCSharp(new[] { libC }, includeDirs: null, defines: null, fileBased: false, libraryMode: true));
        File.WriteAllText(Path.Combine(libDir, AsmName + ".csproj"),
            Compiler.BuildGeneratedCsproj(libraryMode: true, assemblyName: AsmName));

        // ── in-process: the dotcc dlopen consumer (plain managed exe) ──
        var consC = Path.Combine(consDir, "consumer.c");
        File.WriteAllText(consC, dotccConsumerCSource);
        File.WriteAllText(Path.Combine(consDir, "Program.cs"),
            Compiler.EmitCSharp(new[] { consC }, includeDirs: null, defines: null, fileBased: false, libraryMode: false));
        File.WriteAllText(Path.Combine(consDir, ConsumerAsmName + ".csproj"),
            Compiler.BuildGeneratedCsproj(libraryMode: false, assemblyName: ConsumerAsmName));

        // ── one shell script: publish lib → locate .so → build consumer → run it on the .so ──
        // The consumer does NOT dlclose the .so: it is a managed (CoreCLR) program and
        // the .so is a NativeAOT library with its own runtime — unloading it mid-process
        // crashes at teardown (a CoreCLR+NativeAOT coexistence limit, not a dlfcn bug;
        // see SharedLibOracleTests.DotccConsumerSource).
        var script =
            "set -e\n" +
            "cd \"$(dirname \"$0\")\"\n" +
            "arch=$(uname -m); rid=linux-x64; [ \"$arch\" = aarch64 ] && rid=linux-arm64\n" +
            $"if ! dotnet publish 'lib/{AsmName}.csproj' -c Release -r $rid >publish.log 2>&1; then echo PUBLISH_FAILED; tail -25 publish.log; exit 3; fi\n" +
            $"SO=$(find lib/bin -name '{AsmName}.so' | head -1)\n" +
            "if [ -z \"$SO\" ]; then echo NO_SO; find lib/bin -name '*.so'; exit 4; fi\n" +
            "SO=$(readlink -f \"$SO\")\n" +
            $"if ! dotnet build 'consumer/{ConsumerAsmName}.csproj' -c Release -o consumer/out >build.log 2>&1; then echo BUILD_FAILED; tail -25 build.log; exit 5; fi\n" +
            $"echo {OutMarker}\ndotnet \"consumer/out/{ConsumerAsmName}.dll\" \"$SO\"\n";

        return RunCapture(workDir, script);
    }

    /// <summary>
    /// IMPORT-mode round-trip against a gcc-built shared library. <c>gcc -shared
    /// -fPIC</c> compiles <paramref name="libCSource"/> to <c>lib&lt;importName&gt;.so</c>,
    /// then dotcc compiles <paramref name="consumerCSource"/> (plain prototypes + calls,
    /// no <c>dlopen</c>) with <c>-l&lt;importName&gt; -L&lt;workdir&gt;</c> to a managed exe
    /// whose <c>DotCcImports.__BindAll()</c> binds those prototypes to the <c>.so</c>'s
    /// exports at startup. Returns the consumer's stdout — proving the GOT-style binding
    /// calls a real native library through the C ABI. No NativeAOT (the consumer is
    /// managed; only the lib is native), so this needs just <c>dotnet</c> + <c>gcc</c>.
    /// </summary>
    public static string GccLibImportRoundTrip(string libCSource, string importName, string consumerCSource)
    {
        EnsureInitialised();
        if (!_available) { throw new InvalidOperationException("shared-lib oracle is not available on this host."); }

        var workDir = Path.Combine(Path.GetTempPath(), "dotcc-import-gcc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        File.WriteAllText(Path.Combine(workDir, "mylib.c"), libCSource);
        var consC = Path.Combine(workDir, "consumer.c");
        File.WriteAllText(consC, consumerCSource);
        // -L is the workdir AS THE SHELL/runtime sees it (a /mnt/… path on Windows),
        // since the emitted -L string is baked into the program that runs under bash/WSL.
        var imports = new ImportOptions(new[] { importName }, new[] { ToShellPath(workDir) }, Array.Empty<string>());
        File.WriteAllText(Path.Combine(workDir, "Program.cs"),
            Compiler.EmitCSharp(new[] { consC }, includeDirs: null, defines: null,
                fileBased: false, libraryMode: false, imports: imports));
        File.WriteAllText(Path.Combine(workDir, ImportConsumerAsmName + ".csproj"),
            Compiler.BuildGeneratedCsproj(libraryMode: false, assemblyName: ImportConsumerAsmName));

        var script =
            "set -e\n" +
            "cd \"$(dirname \"$0\")\"\n" +
            $"if ! gcc -shared -fPIC mylib.c -o lib{importName}.so 2>gcc.log; then echo GCC_FAILED; cat gcc.log; exit 5; fi\n" +
            $"if ! dotnet build '{ImportConsumerAsmName}.csproj' -c Release -o out >build.log 2>&1; then echo BUILD_FAILED; tail -25 build.log; exit 6; fi\n" +
            $"echo {OutMarker}\ndotnet \"out/{ImportConsumerAsmName}.dll\"\n";

        return RunCapture(workDir, script);
    }

    /// <summary>
    /// IMPORT-mode dotcc-consumes-dotcc round-trip. NativeAOT-publishes
    /// <paramref name="libCSource"/> (dotcc <c>-shared</c>) to <c>&lt;AsmName&gt;.so</c>,
    /// copies it to the workdir, then dotcc compiles <paramref name="consumerCSource"/>
    /// with <c>-l&lt;AsmName&gt; -L&lt;workdir&gt;</c> to a managed exe that binds the
    /// exports GOT-style (no <c>dlopen</c> in the source — the implicit-linking twin of
    /// the dlopen leg). The NativeAOT lib has no <c>lib</c> prefix, so the bind relies on
    /// the loader's <c>&lt;name&gt;.so</c> name variant. Returns the consumer's stdout.
    /// No <c>dlclose</c> is involved (import mode never unloads), so the CoreCLR↔NativeAOT
    /// teardown crash that bars the dlopen leg from unloading doesn't apply here.
    /// </summary>
    public static string DotccLibImportRoundTrip(string libCSource, string consumerCSource)
    {
        EnsureInitialised();
        if (!_available) { throw new InvalidOperationException("shared-lib oracle is not available on this host."); }

        var workDir = Path.Combine(Path.GetTempPath(), "dotcc-import-dl-" + Guid.NewGuid().ToString("N"));
        var libDir = Path.Combine(workDir, "lib");
        var consDir = Path.Combine(workDir, "consumer");
        Directory.CreateDirectory(libDir);
        Directory.CreateDirectory(consDir);

        // ── the -shared lib (NativeAOT) ──
        var libC = Path.Combine(libDir, "lib.c");
        File.WriteAllText(libC, libCSource);
        File.WriteAllText(Path.Combine(libDir, "Program.cs"),
            Compiler.EmitCSharp(new[] { libC }, includeDirs: null, defines: null, fileBased: false, libraryMode: true));
        File.WriteAllText(Path.Combine(libDir, AsmName + ".csproj"),
            Compiler.BuildGeneratedCsproj(libraryMode: true, assemblyName: AsmName));

        // ── the import-mode consumer: -l<AsmName>, -L = workdir (where the .so is copied) ──
        var consC = Path.Combine(consDir, "consumer.c");
        File.WriteAllText(consC, consumerCSource);
        var imports = new ImportOptions(new[] { AsmName }, new[] { ToShellPath(workDir) }, Array.Empty<string>());
        File.WriteAllText(Path.Combine(consDir, "Program.cs"),
            Compiler.EmitCSharp(new[] { consC }, includeDirs: null, defines: null,
                fileBased: false, libraryMode: false, imports: imports));
        File.WriteAllText(Path.Combine(consDir, ImportConsumerAsmName + ".csproj"),
            Compiler.BuildGeneratedCsproj(libraryMode: false, assemblyName: ImportConsumerAsmName));

        var script =
            "set -e\n" +
            "cd \"$(dirname \"$0\")\"\n" +
            "arch=$(uname -m); rid=linux-x64; [ \"$arch\" = aarch64 ] && rid=linux-arm64\n" +
            $"if ! dotnet publish 'lib/{AsmName}.csproj' -c Release -r $rid >publish.log 2>&1; then echo PUBLISH_FAILED; tail -25 publish.log; exit 3; fi\n" +
            $"SO=$(find lib/bin -name '{AsmName}.so' | head -1)\n" +
            "if [ -z \"$SO\" ]; then echo NO_SO; find lib/bin -name '*.so'; exit 4; fi\n" +
            $"cp \"$(readlink -f \"$SO\")\" \"./{AsmName}.so\"\n" +
            $"if ! dotnet build 'consumer/{ImportConsumerAsmName}.csproj' -c Release -o consumer/out >build.log 2>&1; then echo BUILD_FAILED; tail -25 build.log; exit 5; fi\n" +
            $"echo {OutMarker}\ndotnet \"consumer/out/{ImportConsumerAsmName}.dll\"\n";

        return RunCapture(workDir, script);
    }

    /// <summary>
    /// IMPORT-mode round-trip against a STATIC archive. <c>gcc -c</c> + <c>ar rcs</c>
    /// build <c>libmylib.a</c>, then dotcc compiles <paramref name="consumerCSource"/>
    /// (plain prototypes + calls) with the archive as a direct input → <c>[DllImport]</c>
    /// stubs + a csproj with <c>&lt;DirectPInvoke&gt;</c>/<c>&lt;NativeLibrary&gt;</c>, which
    /// NativeAOT-publish links statically into a native exe. Runs the exe and returns its
    /// stdout — the proof that static linking resolves the stubs against the archive.
    /// (Static linking is publish-only; there's no <c>dotnet run</c> path.)
    /// </summary>
    public static string StaticArchiveImportRoundTrip(string libCSource, string consumerCSource)
    {
        EnsureInitialised();
        if (!_available) { throw new InvalidOperationException("shared-lib oracle is not available on this host."); }

        var workDir = Path.Combine(Path.GetTempPath(), "dotcc-import-static-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        File.WriteAllText(Path.Combine(workDir, "mylib.c"), libCSource);
        var consC = Path.Combine(workDir, "consumer.c");
        File.WriteAllText(consC, consumerCSource);
        // The archive path AS THE SHELL/linker sees it (ar builds it there, publish links
        // it there). dotcc derives the DllImport/<DirectPInvoke> name ("mylib") from the
        // filename and writes <NativeLibrary Include="…/libmylib.a"> into the csproj.
        var archivePath = ToShellPath(workDir) + "/libmylib.a";
        var imports = new ImportOptions(Array.Empty<string>(), Array.Empty<string>(), new[] { archivePath });
        File.WriteAllText(Path.Combine(workDir, "Program.cs"),
            Compiler.EmitCSharp(new[] { consC }, includeDirs: null, defines: null,
                fileBased: false, libraryMode: false, imports: imports));
        File.WriteAllText(Path.Combine(workDir, ImportConsumerAsmName + ".csproj"),
            Compiler.BuildGeneratedCsproj(libraryMode: false, assemblyName: ImportConsumerAsmName,
                staticArchives: new[] { archivePath }));

        var script =
            "set -e\n" +
            "cd \"$(dirname \"$0\")\"\n" +
            "arch=$(uname -m); rid=linux-x64; [ \"$arch\" = aarch64 ] && rid=linux-arm64\n" +
            "if ! gcc -c -fPIC mylib.c -o mylib.o 2>gcc.log; then echo GCC_FAILED; cat gcc.log; exit 5; fi\n" +
            "if ! ar rcs libmylib.a mylib.o 2>ar.log; then echo AR_FAILED; cat ar.log; exit 6; fi\n" +
            $"if ! dotnet publish '{ImportConsumerAsmName}.csproj' -c Release -r $rid >publish.log 2>&1; then echo PUBLISH_FAILED; tail -30 publish.log; exit 3; fi\n" +
            $"EXE=$(find bin -type f -name '{ImportConsumerAsmName}' -path '*publish*' | head -1)\n" +
            "if [ -z \"$EXE\" ]; then echo NO_EXE; find bin -path '*publish*' -type f; exit 4; fi\n" +
            $"echo {OutMarker}\n\"$EXE\"\n";

        return RunCapture(workDir, script);
    }

    /// <summary>Write <paramref name="scriptBody"/> to <c>run.sh</c> in
    /// <paramref name="workDir"/>, run it (<c>bash &lt;file&gt;</c>), and return the
    /// consumer's own stdout — everything after the <see cref="OutMarker"/> line the
    /// script echoes right before invoking the consumer (so publish/build chatter
    /// before it is discarded). Throws with the full captured log on a non-zero exit
    /// or a missing marker.</summary>
    /// <remarks>The script runs from a FILE rather than inline (<c>bash -lc
    /// '&lt;body&gt;'</c>): the inline form must survive .NET → <c>wsl.exe</c> → bash
    /// argument quoting on the Windows dev box, which mangles the embedded
    /// double-quotes the script needs (<c>[ "$x" = … ]</c>, <c>"$SO"</c>). A file path
    /// carries none, so the transport is robust on both native bash (CI) and wsl.exe.</remarks>
    private static string RunCapture(string workDir, string scriptBody)
    {
        var scriptPath = Path.Combine(workDir, "run.sh");
        // LF endings so bash on Linux/WSL doesn't choke on CRs.
        File.WriteAllText(scriptPath, scriptBody.Replace("\r\n", "\n"));
        var (stdout, stderr, exit) = RunShell($"bash '{ToShellPath(scriptPath)}'");
        if (exit != 0 || !stdout.Contains(OutMarker))
        {
            throw new InvalidOperationException(
                $"shared-lib oracle failed (exit {exit}).\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        }
        var idx = stdout.IndexOf(OutMarker, StringComparison.Ordinal);
        return stdout[(idx + OutMarker.Length)..].TrimStart('\r', '\n');
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
