#nullable enable

using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text;

namespace DotCC;

/// <summary>
/// dotcc frontend — clang-shaped CLI over <see cref="Compiler"/>. Owns only
/// argument parsing, file I/O, and process exit codes; all parsing /
/// preprocessing / emitting happens in <c>DotCC.Lib</c>.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var inputArg = new Argument<string[]>("inputs")
        {
            Description = "C source files (.c) to compile. .h files are pulled in via #include only.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var outOpt = new Option<string?>("-o", "--output")
        {
            Description = "Output path. Default: ./a.out-cs/ for csproj/build mode, stdout for --emit=csharp.",
        };
        var emitOpt = new Option<EmitKind>("--emit")
        {
            Description = "csproj: write Program.cs + .csproj. file: emit a single .NET 10 file-based program to stdout. build: like csproj, then `dotnet build`. obj: compile one .c to a .cs object fragment (link .cs objects to a program).",
            DefaultValueFactory = _ => EmitKind.Csproj,
        };
        var targetOpt = new Option<string?>("--target")
        {
            Description = "Output target (the M in N×M): cs (C#, default) or wat (WebAssembly text). wat emits a .wat module to -o, else stdout.",
        };
        var preprocessOpt = new Option<bool>("-E")
        {
            Description = "Run the preprocessor only; emit the post-#include/#define token stream to stdout.",
        };
        var includeOpt = new Option<string[]>("-I")
        {
            Description = "Add directory to header search path. Repeatable.",
            AllowMultipleArgumentsPerToken = false,
        };
        var defineOpt = new Option<string[]>("-D")
        {
            Description = "Predefine macro (NAME or NAME=VALUE). Repeatable.",
            AllowMultipleArgumentsPerToken = false,
        };
        var compileOpt = new Option<bool>("-c")
        {
            Description = "Compile to a .NET assembly (no native publish). Clang-shaped alias for --emit=build.",
        };
        var sharedOpt = new Option<bool>("-shared")
        {
            Description = "Produce a shared library (NativeAOT-publishable, with [UnmanagedCallersOnly] exports for non-static C functions).",
        };
        var stdOpt = new Option<string?>("-std")
        {
            Description = "C dialect: c90/c99/c11/c17/c18/c23. Default: c17.",
        };
        var pedanticOpt = new Option<bool>("-pedantic")
        {
            Description = "Warn on language features newer than the selected -std (e.g. // comments are not gated, but designated initializers under c90 warn).",
        };
        var pedanticErrorsOpt = new Option<bool>("-pedantic-errors")
        {
            Description = "Like -pedantic, but treat the dialect violations as errors (non-zero exit).",
        };
        var wconversionOpt = new Option<bool>("-Wconversion")
        {
            Description = "Warn on implicit integer conversions that narrow (a wider value stored into a narrower type, e.g. int -> unsigned char). Off by default, like gcc/clang -Wconversion.",
        };
        var wnoDiscardedQualifiersOpt = new Option<bool>("-Wno-discarded-qualifiers")
        {
            Description = "Suppress the warning for an implicit conversion that discards a const qualifier from a pointer's pointee (passing/assigning/returning a const T* where a T* is expected). On by default, like gcc -Wdiscarded-qualifiers. Does NOT affect the write-to-const error.",
        };
        var sanitizeOpt = new Option<string?>("-fsanitize")
        {
            Description = "Enable a sanitizer. Only 'address' is modeled: routes the emitted program's malloc/calloc/realloc/free through a checked debug heap (redzone overflow + bad/double-free detection, a heap-only subset of clang's -fsanitize=address). DOTCC_DEBUG_HEAP=1 is the runtime-override equivalent.",
        };
        var mdOpt = new Option<bool>("-MD")
        {
            Description = "Write a Make-format dependency file (.d) listing the TU + every #included header, alongside compilation.",
        };
        var mmdOpt = new Option<bool>("-MMD")
        {
            Description = "Like -MD, but omit system (<...>) headers from the dependency file.",
        };
        var mfOpt = new Option<string?>("-MF")
        {
            Description = "Dependency-file output path (with -MD/-MMD). Defaults to the object/source name with a .d extension.",
        };
        var mtOpt = new Option<string[]>("-MT")
        {
            Description = "Set the target name(s) of the emitted dependency rule. Repeatable. Defaults to the output object.",
            AllowMultipleArgumentsPerToken = false,
        };
        var linkOpt = new Option<string[]>("-l")
        {
            Description = "Link against a native library (import mode): an undefined, called prototype is bound to this library's export at startup. Repeatable; the glued -lfoo form is accepted too.",
            AllowMultipleArgumentsPerToken = false,
        };
        var libDirOpt = new Option<string[]>("-L")
        {
            Description = "Add a directory to the native-library (-l) search path. Repeatable; the glued -L/dir form is accepted too.",
            AllowMultipleArgumentsPerToken = false,
        };
        var root = new RootCommand("dotcc — a C compiler frontend that transpiles to .NET 10 / C# 14.")
        {
            inputArg, outOpt, emitOpt, targetOpt, preprocessOpt, includeOpt, defineOpt, compileOpt, sharedOpt, stdOpt,
            pedanticOpt, pedanticErrorsOpt, wconversionOpt, wnoDiscardedQualifiersOpt, sanitizeOpt, mdOpt, mmdOpt, mfOpt, mtOpt, linkOpt, libDirOpt,
        };
        // Accept-and-ignore unknown flags (-Wall, -O2, -g, -f*, -m*, …) instead
        // of erroring out, so dotcc survives being driven by ./configure / make,
        // which pass a grab-bag of gcc/clang flags dotcc doesn't model. Unknown
        // tokens land in parse.UnmatchedTokens; we warn once and carry on.
        root.TreatUnmatchedTokensAsErrors = false;

        root.SetAction(parse =>
        {
            var rawInputs = parse.GetValue(inputArg) ?? Array.Empty<string>();
            var linkLibs = (parse.GetValue(linkOpt) ?? Array.Empty<string>()).ToList();
            var libDirs = (parse.GetValue(libDirOpt) ?? Array.Empty<string>()).ToList();
            // A string[] argument greedily swallows unknown `-`-prefixed flags
            // (gcc/clang noise like -Wall/-O2/-g), so partition them out of the
            // input-file list and warn rather than trying to open them as files.
            // (Unmatched value-less options also land here via UnmatchedTokens.)
            // FIRST harvest glued import flags (`-lfoo` / `-L/dir`): the spaced
            // forms are consumed by the options above, but a glued one can land
            // here as an unmatched token, and it must NOT be mistaken for noise.
            foreach (var tok in rawInputs.Where(t => t.StartsWith('-')).Concat(parse.UnmatchedTokens))
            {
                if (tok.Length > 2 && tok.StartsWith("-l", StringComparison.Ordinal)) { linkLibs.Add(tok[2..]); }
                else if (tok.Length > 2 && tok.StartsWith("-L", StringComparison.Ordinal)) { libDirs.Add(tok[2..]); }
                else { Console.Error.WriteLine($"dotcc: warning: ignoring unsupported option '{tok}'"); }
            }
            var allInputs = rawInputs.Where(t => !t.StartsWith('-')).ToArray();
            // Static archives (.a/.lib) are link inputs, not translation units — partition
            // them into the import options (absolute-pathed) rather than the source list.
            static bool IsArchive(string p) =>
                p.EndsWith(".a", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".lib", StringComparison.OrdinalIgnoreCase);
            var staticArchives = allInputs.Where(IsArchive).Select(Path.GetFullPath).ToArray();
            var inputs = allInputs.Where(p => !IsArchive(p)).ToArray();
            var imports = new ImportOptions(linkLibs, libDirs, staticArchives);
            var output = parse.GetValue(outOpt);
            var emit = parse.GetValue(emitOpt);
            var target = parse.GetValue(targetOpt);
            var preprocessOnly = parse.GetValue(preprocessOpt);
            var includes = parse.GetValue(includeOpt) ?? Array.Empty<string>();
            var defines = parse.GetValue(defineOpt) ?? Array.Empty<string>();
            var compileFlag = parse.GetValue(compileOpt);
            var sharedFlag = parse.GetValue(sharedOpt);
            var stdValue = parse.GetValue(stdOpt);
            // Fold the individual -W / -pedantic flags into one WarningFlags value
            // threaded through the pipeline (see DotCC.WarningFlags).
            var warnings = WarningFlags.None;
            if (!parse.GetValue(wnoDiscardedQualifiersOpt)) { warnings |= WarningFlags.DiscardedQualifiers; }
            if (parse.GetValue(wconversionOpt)) { warnings |= WarningFlags.Conversion; }
            if (parse.GetValue(pedanticErrorsOpt)) { warnings |= WarningFlags.PedanticErrors; }
            else if (parse.GetValue(pedanticOpt)) { warnings |= WarningFlags.Pedantic; }
            // -fsanitize=address[,...]: enable the checked debug heap. Other
            // sanitizers aren't modeled — warn and carry on rather than error
            // (./configure probes sanitizers and shouldn't fail the build).
            var sanitizeValue = parse.GetValue(sanitizeOpt);
            var debugHeapFlag = false;
            if (sanitizeValue is not null)
            {
                var kinds = sanitizeValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                debugHeapFlag = kinds.Contains("address");
                foreach (var k in kinds.Where(k => k != "address"))
                {
                    Console.Error.WriteLine($"dotcc: warning: ignoring unsupported -fsanitize={k}");
                }
            }
            var mdFlag = parse.GetValue(mdOpt);
            var mmdFlag = parse.GetValue(mmdOpt);
            var depFile = parse.GetValue(mfOpt);
            var depTargets = parse.GetValue(mtOpt) ?? Array.Empty<string>();

            if (inputs.Length == 0)
            {
                Console.Error.WriteLine("dotcc: error: no input files");
                return 1;
            }

            CDialect dialect;
            try
            {
                dialect = stdValue is null ? CDialect.Default : CDialect.Parse(stdValue);
            }
            catch (FormatException ex)
            {
                Console.Error.WriteLine($"dotcc: error: {ex.Message}");
                return 1;
            }

            // -c is a clang-shaped alias for --emit=build (compile to assembly).
            // It overrides --emit only when the user didn't explicitly pick
            // something else.
            if (compileFlag && emit == EmitKind.Csproj)
            {
                emit = EmitKind.Build;
            }

            // Infer the emit kind from the -o shape when none was chosen
            // (symmetric with the -o inference below): `-o foo.cs` means a single
            // file-based program; a directory-ish path stays csproj. `obj` is
            // never inferred — you ask for it with --emit=obj (or -c).
            if (emit == EmitKind.Csproj && output is not null
                && output.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                emit = EmitKind.File;
            }

            return Run(inputs, output, emit, target, preprocessOnly, includes, defines, sharedFlag, dialect,
                       mdFlag, mmdFlag, depFile, depTargets, debugHeapFlag, imports, warnings);
        });

        return root.Parse(args).Invoke();
    }

    private enum EmitKind { Csproj, File, Build, Obj }

    private static int Run(
        string[] inputPaths,
        string? outputPath,
        EmitKind emit,
        string? target,
        bool preprocessOnly,
        string[] includeDirs,
        string[] defines,
        bool libraryMode,
        CDialect dialect,
        bool genDeps,
        bool genDepsNoSystem,
        string? depFile,
        string[] depTargets,
        bool debugHeap = false,
        ImportOptions? imports = null,
        WarningFlags warnings = WarningFlags.Default)
    {
        imports ??= ImportOptions.Empty;
        if (preprocessOnly)
        {
            Compiler.Preprocess(inputPaths, Console.Out, includeDirs, defines, dialect);
            return 0;
        }

        if (target is not null
            && !target.Equals("cs", StringComparison.OrdinalIgnoreCase)
            && !target.Equals("wat", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"dotcc: error: unknown --target '{target}' (expected cs or wat)");
            return 1;
        }
        if (string.Equals(target, "wat", StringComparison.OrdinalIgnoreCase))
        {
            if (imports is { HasAny: true })
            {
                Console.Error.WriteLine("dotcc: warning: -l/-L native library imports are ignored for --target=wat");
            }
            return RunWat(inputPaths, outputPath, includeDirs, defines, dialect, warnings);
        }

        // Separate compilation: `--emit=obj a.c -o a.cs` compiles ONE translation
        // unit to a `.cs` object fragment (no shell/runtime); a later link step
        // merges objects. This is what a CMake/make toolchain calls per file.
        if (emit == EmitKind.Obj)
        {
            if (inputPaths.Length != 1)
            {
                Console.Error.WriteLine("dotcc: --emit=obj compiles one .c at a time");
                return 2;
            }
            // Infer -o from the source when omitted: `foo.c` → `foo.cs` in the
            // current dir (clang's `cc -c foo.c` → `foo.o` convention).
            var objOut = outputPath ?? Path.ChangeExtension(Path.GetFileName(inputPaths[0]), ".cs");
            try
            {
                var frag = Compiler.EmitObject(inputPaths[0], includeDirs, defines, dialect, warnings);
                File.WriteAllText(objOut, frag);
            }
            catch (CompileException ex)
            {
                Console.Error.WriteLine($"dotcc: {ex.Message}");
                return 2;
            }
            // -MD/-MMD: write the header-dependency rule for this TU. The
            // object IS the rule target by default (gcc's `foo.o → foo.d`
            // convention, here `foo.cs → foo.d`); -MT / -MF override both.
            if (genDeps || genDepsNoSystem)
            {
                return WriteDependencyFiles(
                    new[] { inputPaths[0] }, includeSystem: !genDepsNoSystem, depFile, depTargets,
                    defaultTargetFor: _ => objOut,
                    defaultDepPathFor: _ => Path.ChangeExtension(objOut, ".d"),
                    includeDirs, defines, dialect);
            }
            return 0;
        }

        // A `.c` (C) or `.zig` (Zig) input is a source (→ whole-program compile, the
        // default); anything else is a dotcc object fragment (→ link). The object
        // suffix is the build system's choice — CMake's `Generic` platform names them
        // `.obj`, gcc-style `.o`, dotcc-by-hand `.cs` — so we key off "not a source"
        // rather than a fixed object extension. (Compiler.EmitCSharp then dispatches
        // by extension to the C or Zig front-end behind the IFrontend seam.)
        var linking = inputPaths.Length > 0
            && System.Array.TrueForAll(inputPaths, p =>
                !p.EndsWith(".c", System.StringComparison.OrdinalIgnoreCase)
                && !p.EndsWith(".zig", System.StringComparison.OrdinalIgnoreCase));
        string program;
        try
        {
            program = linking
                ? Compiler.LinkObjects(inputPaths, fileBased: emit == EmitKind.File && !libraryMode, libraryMode: libraryMode, debugHeap: debugHeap, imports: imports)
                : Compiler.EmitCSharp(
                    inputPaths,
                    includeDirs,
                    defines,
                    fileBased: emit == EmitKind.File && !libraryMode,
                    libraryMode: libraryMode,
                    dialect: dialect,
                    debugHeap: debugHeap,
                    imports: imports,
                    warnings: warnings);
        }
        catch (CompileException ex)
        {
            Console.Error.WriteLine($"dotcc: {ex.Message}");
            return 2;
        }

        // -MD/-MMD outside obj mode: write one dependency rule per `.c` source
        // (a pure link has no source to scan, so it's skipped). Default target
        // and depfile follow gcc's source-basename `.cs`/`.d` convention; a
        // single -MF names the file when there's one source.
        if (!linking && (genDeps || genDepsNoSystem))
        {
            var sources = inputPaths.Where(p => p.EndsWith(".c", StringComparison.OrdinalIgnoreCase)).ToArray();
            var rc = WriteDependencyFiles(
                sources, includeSystem: !genDepsNoSystem, depFile, depTargets,
                defaultTargetFor: s => Path.ChangeExtension(Path.GetFileName(s), ".cs"),
                defaultDepPathFor: s => Path.ChangeExtension(Path.GetFileName(s), ".d"),
                includeDirs, defines, dialect);
            if (rc != 0) { return rc; }
        }

        var outDir = outputPath ?? "a.out-cs";
        // Assembly name: last component of -o path (clang convention: -o foo → foo.exe)
        var asmName = Path.GetFileName(outDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (asmName.Length == 0) asmName = "a.out";
        switch (emit)
        {
            case EmitKind.File:
                // A single file-based program: write to -o when given (the
                // `-o foo.cs` inference), else to stdout (pipe-to-a-.cs).
                if (outputPath is not null)
                {
                    File.WriteAllText(outputPath, program);
                    Console.Error.WriteLine($"dotcc: wrote {outputPath}");
                }
                else
                {
                    Console.WriteLine(program);
                }
                return 0;

            case EmitKind.Csproj:
            case EmitKind.Build:
            {
                Directory.CreateDirectory(outDir);
                var csprojFile = $"{asmName}.csproj";
                File.WriteAllText(Path.Combine(outDir, "Program.cs"), program);
                File.WriteAllText(Path.Combine(outDir, csprojFile), Compiler.BuildGeneratedCsproj(libraryMode, asmName, imports.StaticArchives));
                Console.Error.WriteLine($"dotcc: wrote {outDir}/Program.cs + {csprojFile}");
                if (imports.StaticArchives.Count > 0)
                {
                    // Static archives link only at NativeAOT publish — even `--emit=build`
                    // (dotnet build) leaves the [DllImport] stubs unresolved at runtime.
                    Console.Error.WriteLine(
                        $"dotcc: note: static native archives ({string.Join(", ", imports.StaticArchives)}) " +
                        $"link at NativeAOT publish — run `dotnet publish -c Release -r <RID>` in {outDir}/");
                }
                if (emit == EmitKind.Build)
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "dotnet",
                        WorkingDirectory = outDir,
                    };
                    psi.ArgumentList.Add("build");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add("Release");
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc!.WaitForExit();
                    if (proc.ExitCode != 0) { return proc.ExitCode; }
                    var artifactNote = libraryMode
                        ? $"managed .dll at {outDir}/bin/Release/net10.0/{asmName}.dll. Run `dotnet publish -c Release` in {outDir}/ for the native shared library."
                        : $"dotnet {outDir}/bin/Release/net10.0/{asmName}.dll [args]";
                    Console.Error.WriteLine($"dotcc: OK. {artifactNote}");
                }
                return 0;
            }

            default:
                return 1;
        }
    }

    /// <summary>
    /// Compile the <c>.c</c> inputs to a WebAssembly-text module and write it to
    /// <c>-o</c> (else stdout). Whole-program, like the default C# path; linking of
    /// pre-compiled wasm objects isn't a thing yet (milestone-2+).
    /// </summary>
    private static int RunWat(
        string[] inputPaths,
        string? outputPath,
        string[] includeDirs,
        string[] defines,
        CDialect dialect,
        WarningFlags warnings)
    {
        var sources = inputPaths.Where(p => p.EndsWith(".c", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sources.Length == 0)
        {
            Console.Error.WriteLine("dotcc: error: --target=wat needs .c source input");
            return 1;
        }
        string wat;
        try
        {
            wat = Compiler.EmitWat(sources, includeDirs, defines, dialect, warnings);
        }
        catch (CompileException ex)
        {
            Console.Error.WriteLine($"dotcc: {ex.Message}");
            return 2;
        }
        if (outputPath is not null)
        {
            File.WriteAllText(outputPath, wat);
            Console.Error.WriteLine($"dotcc: wrote {outputPath}");
        }
        else
        {
            Console.WriteLine(wat);
        }
        return 0;
    }

    /// <summary>
    /// Write a Make-format dependency file (<c>-MD</c>/<c>-MMD</c>) for each
    /// source. The rule target(s) come from <c>-MT</c> (or
    /// <paramref name="defaultTargetFor"/>); the output path from a single
    /// <c>-MF</c> (or <paramref name="defaultDepPathFor"/>). With multiple
    /// sources and no <c>-MF</c>, each gets its own default <c>.d</c> so the
    /// rules don't clobber one another.
    /// </summary>
    private static int WriteDependencyFiles(
        string[] sourcePaths,
        bool includeSystem,
        string? depFile,
        string[] depTargets,
        Func<string, string> defaultTargetFor,
        Func<string, string> defaultDepPathFor,
        string[] includeDirs,
        string[] defines,
        CDialect dialect)
    {
        foreach (var src in sourcePaths)
        {
            var targets = depTargets.Length > 0 ? depTargets : new[] { defaultTargetFor(src) };
            var path = depFile is not null && sourcePaths.Length == 1
                ? depFile
                : defaultDepPathFor(src);
            try
            {
                var rule = Compiler.EmitDependencyRule(src, targets, includeSystem, includeDirs, defines, dialect);
                File.WriteAllText(path, rule);
            }
            catch (CompileException ex)
            {
                Console.Error.WriteLine($"dotcc: {ex.Message}");
                return 2;
            }
        }
        return 0;
    }
}
