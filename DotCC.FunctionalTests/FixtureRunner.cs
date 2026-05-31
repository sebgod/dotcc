#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotCC.FunctionalTests;

/// <summary>
/// In-process pipeline: dotcc → Roslyn → load → invoke. Pure functions, no
/// disk IO besides reading fixture sources.
/// </summary>
internal static class FixtureRunner
{
    // Console.Out is process-global state; redirecting it isn't thread-safe.
    // Two parallel test theories iterating Fixtures/ would race on the
    // redirect (one finishes, restores prevOut which was already overwritten
    // by the second). Serialize the redirect-run-restore window so each
    // capture sees only its own program's output. The Roslyn compile above
    // stays parallel — the slow part of the pipeline doesn't need the lock.
    private static readonly object _consoleLock = new();

    /// <summary>
    /// Discover fixtures: each subdir of <c>Fixtures/</c> next to the test
    /// assembly that contains at least one <c>.c</c> file and an
    /// <c>expected-stdout.txt</c> sidecar. An optional <c>std.txt</c> sidecar
    /// (contents e.g. <c>c23</c>) selects the dialect for that fixture;
    /// absent, it defaults to dotcc's default (<c>c17</c>).
    /// </summary>
    public static IEnumerable<(string name, string dir, string[] sources, string expectedStdout, string std)> Discover()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        if (!Directory.Exists(root)) { yield break; }
        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(p => p, StringComparer.Ordinal))
        {
            var sources = Directory.EnumerateFiles(dir, "*.c").OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var expectedPath = Path.Combine(dir, "expected-stdout.txt");
            if (sources.Length == 0 || !File.Exists(expectedPath)) { continue; }
            var stdPath = Path.Combine(dir, "std.txt");
            var std = File.Exists(stdPath) ? File.ReadAllText(stdPath).Trim() : "c17";
            yield return (Path.GetFileName(dir), dir, sources, File.ReadAllText(expectedPath), std);
        }
    }

    /// <summary>
    /// Compile <paramref name="csharpSource"/> with Roslyn into an in-memory
    /// console-app assembly, invoke its entry point with the given args, and
    /// return whatever it wrote to stdout. Throws if the C# fails to compile
    /// (with the Roslyn diagnostics in the message — that's a dotcc emit bug,
    /// not a fixture bug).
    /// </summary>
    public static string CompileAndRun(string csharpSource, string[] args)
    {
        // Strip the file-based-program directive — Roslyn doesn't parse it
        // (it's a `dotnet run --file` thing). The rest of the source compiles
        // unchanged as a regular console app.
        var cleaned = StripFileBasedHeader(csharpSource);

        var syntax = CSharpSyntaxTree.ParseText(cleaned,
            new CSharpParseOptions(LanguageVersion.Preview));

        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();
        // The spliced DotCC.Libc runtime block references
        // System.Diagnostics.Process (for system()). It's type-forwarded and
        // isn't in the harvested set above, so add it explicitly (dedup by
        // path). At AOT publish / `dotnet run` this is free — the full
        // framework ref set already includes it.
        AddReferenceByType(refs, typeof(System.Diagnostics.Process));

        var options = new CSharpCompilationOptions(
            OutputKind.ConsoleApplication,
            allowUnsafe: true,
            optimizationLevel: OptimizationLevel.Debug,
            // Top-level statements + the inline ref struct + utf8 string literals
            // exercise a few language features that nullable-warning analysis
            // can otherwise grumble about. Turn warnings to off — tests assert
            // on behavior, not warning posture.
            nullableContextOptions: NullableContextOptions.Disable);

        var comp = CSharpCompilation.Create(
            assemblyName: "DotCCEmitted_" + Guid.NewGuid().ToString("N"),
            syntaxTrees: new[] { syntax },
            references: refs,
            options: options);

        using var pe = new MemoryStream();
        var result = comp.Emit(pe);
        if (!result.Success)
        {
            var errs = string.Join('\n',
                result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString()));
            throw new InvalidOperationException("Roslyn rejected emitted C#:\n" + errs + "\n\n--- source ---\n" + cleaned);
        }
        pe.Position = 0;

        // Fresh ALC so each fixture run is isolated.
        var alc = new AssemblyLoadContext($"dotcc-fixture-{Guid.NewGuid():N}", isCollectible: true);
        var asm = alc.LoadFromStream(pe);
        var entry = asm.EntryPoint
            ?? throw new InvalidOperationException("emitted assembly has no entry point");

        var captured = new StringWriter();
        lock (_consoleLock)
        {
            var prevOut = Console.Out;
            Console.SetOut(captured);
            try
            {
                // Top-level-statements entry point: `<Main>$(string[] args)`.
                // The arity check below tolerates either no-arg or string[]-arg signatures.
                var pars = entry.GetParameters();
                object?[] invokeArgs = pars.Length switch
                {
                    0 => Array.Empty<object?>(),
                    1 => new object?[] { args },
                    _ => throw new InvalidOperationException($"unexpected entry-point arity {pars.Length}"),
                };
                entry.Invoke(null, invokeArgs);
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                throw tie.InnerException;
            }
            finally
            {
                Console.SetOut(prevOut);
            }
        }
        return captured.ToString();
    }

    /// <summary>
    /// Append the assembly that defines <paramref name="type"/> to the Roslyn
    /// reference set, unless a reference with the same file path is already
    /// present. Used to pull in type-forwarded framework assemblies (e.g.
    /// System.Diagnostics.Process, referenced by the spliced runtime block for
    /// system()) that <c>AppDomain.GetAssemblies()</c> doesn't surface.
    /// </summary>
    internal static void AddReferenceByType(List<MetadataReference> refs, Type type)
    {
        var loc = type.Assembly.Location;
        if (string.IsNullOrEmpty(loc)) { return; }
        bool present = refs.Any(r =>
            r is PortableExecutableReference pe &&
            string.Equals(pe.FilePath, loc, StringComparison.OrdinalIgnoreCase));
        if (!present) { refs.Add(MetadataReference.CreateFromFile(loc)); }
    }

    private static string StripFileBasedHeader(string source)
    {
        var sb = new StringBuilder(source.Length);
        var sawNonDirective = false;
        foreach (var line in source.Split('\n'))
        {
            if (!sawNonDirective && line.StartsWith("#:", StringComparison.Ordinal))
            {
                continue;
            }
            sawNonDirective = true;
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }
}
