#nullable enable

using System;
using System.CommandLine;
using System.IO;
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
            Description = "csproj: write Program.cs + .csproj. csharp: emit a single .NET 10 file-based program to stdout. build: like csproj, then `dotnet build`.",
            DefaultValueFactory = _ => EmitKind.Csproj,
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

        var root = new RootCommand("dotcc — a C compiler frontend that transpiles to .NET 10 / C# 14.")
        {
            inputArg, outOpt, emitOpt, preprocessOpt, includeOpt, defineOpt, compileOpt, sharedOpt, stdOpt,
            pedanticOpt, pedanticErrorsOpt,
        };

        root.SetAction(parse =>
        {
            var inputs = parse.GetValue(inputArg) ?? Array.Empty<string>();
            var output = parse.GetValue(outOpt);
            var emit = parse.GetValue(emitOpt);
            var preprocessOnly = parse.GetValue(preprocessOpt);
            var includes = parse.GetValue(includeOpt) ?? Array.Empty<string>();
            var defines = parse.GetValue(defineOpt) ?? Array.Empty<string>();
            var compileFlag = parse.GetValue(compileOpt);
            var sharedFlag = parse.GetValue(sharedOpt);
            var stdValue = parse.GetValue(stdOpt);
            var pedanticFlag = parse.GetValue(pedanticOpt);
            var pedanticErrorsFlag = parse.GetValue(pedanticErrorsOpt);

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

            return Run(inputs, output, emit, preprocessOnly, includes, defines, sharedFlag, dialect, pedanticFlag, pedanticErrorsFlag);
        });

        return root.Parse(args).Invoke();
    }

    private enum EmitKind { Csproj, Csharp, Build }

    private static int Run(
        string[] inputPaths,
        string? outputPath,
        EmitKind emit,
        bool preprocessOnly,
        string[] includeDirs,
        string[] defines,
        bool libraryMode,
        CDialect dialect,
        bool pedantic,
        bool pedanticErrors)
    {
        if (preprocessOnly)
        {
            Compiler.Preprocess(inputPaths, Console.Out, includeDirs, defines, dialect);
            return 0;
        }

        string program;
        try
        {
            program = Compiler.EmitCSharp(
                inputPaths,
                includeDirs,
                defines,
                fileBased: emit == EmitKind.Csharp && !libraryMode,
                libraryMode: libraryMode,
                dialect: dialect,
                pedantic: pedantic,
                pedanticErrors: pedanticErrors);
        }
        catch (CompileException ex)
        {
            Console.Error.WriteLine($"dotcc: {ex.Message}");
            return 2;
        }

        var outDir = outputPath ?? "a.out-cs";
        switch (emit)
        {
            case EmitKind.Csharp:
                Console.WriteLine(program);
                return 0;

            case EmitKind.Csproj:
            case EmitKind.Build:
            {
                Directory.CreateDirectory(outDir);
                File.WriteAllText(Path.Combine(outDir, "Program.cs"), program);
                File.WriteAllText(Path.Combine(outDir, "dotcc-out.csproj"), Compiler.BuildGeneratedCsproj(libraryMode));
                Console.Error.WriteLine($"dotcc: wrote {outDir}/Program.cs + dotcc-out.csproj");
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
                        ? $"managed .dll at {outDir}/bin/Release/net10.0/dotcc-out.dll. Run `dotnet publish -c Release` in {outDir}/ for the native shared library."
                        : $"dotnet {outDir}/bin/Release/net10.0/dotcc-out.dll [args]";
                    Console.Error.WriteLine($"dotcc: OK. {artifactNote}");
                }
                return 0;
            }

            default:
                return 1;
        }
    }
}
