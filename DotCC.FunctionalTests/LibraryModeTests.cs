#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// Library-mode (<c>-shared</c>) end-to-end tests: drives
/// <c>Compiler.EmitCSharp(libraryMode: true)</c> on an inline C string,
/// compiles the result with Roslyn as a <c>DynamicallyLinkedLibrary</c>,
/// loads the assembly, and reflects on the metadata to verify:
/// <list type="bullet">
///   <item>Non-static C functions show up under <c>DotCcExports</c>
///     with the <c>[UnmanagedCallersOnly]</c> attribute and the expected
///     <c>EntryPoint</c> name.</item>
///   <item><c>static</c> C functions are NOT exported.</item>
///   <item>Calls between user functions inside <c>DotCcLib</c> are direct
///     method calls (no function-pointer dance needed).</item>
/// </list>
/// We don't run the actual NativeAOT publish here — that's a 30+ second
/// pipeline step. These tests only verify that the C# emit shape is
/// well-formed and the metadata matches what NativeAOT will export.
/// </summary>
public sealed class LibraryModeTests
{
    [Fact]
    public void Lib_mode_emits_unmanagedcallersonly_for_extern_functions()
    {
        var tempC = Path.GetTempFileName() + ".c";
        File.WriteAllText(tempC, """
            int add(int a, int b) { return a + b; }
            double scale(double v, double k) { return v * k; }

            static int helper(int x) { return x * 2; }

            int double_it(int x) { return helper(x); }
            """);
        try
        {
            var program = Compiler.EmitCSharp(
                new[] { tempC },
                includeDirs: null,
                defines: null,
                fileBased: false,
                libraryMode: true);

            // Quick textual sanity checks before doing the heavier Roslyn compile.
            program.ShouldContain("internal static class DotCcLib");
            program.ShouldContain("public static class DotCcExports");
            program.ShouldContain("[UnmanagedCallersOnly(EntryPoint = \"add\"");
            program.ShouldContain("[UnmanagedCallersOnly(EntryPoint = \"scale\"");
            program.ShouldContain("[UnmanagedCallersOnly(EntryPoint = \"double_it\"");
            // helper is `static` — internal linkage, no export.
            program.ShouldNotContain("EntryPoint = \"helper\"");

            // Compile as a library and reflect.
            var asm = CompileLibrary(program);

            var exports = asm.GetType("DotCcExports", throwOnError: true)!;
            var dotCcLib = asm.GetType("DotCcLib", throwOnError: true)!;

            // Each export should have UnmanagedCallersOnly with EntryPoint == name.
            AssertExport(exports, name: "add", expectedParamCount: 2);
            AssertExport(exports, name: "scale", expectedParamCount: 2);
            AssertExport(exports, name: "double_it", expectedParamCount: 1);

            // helper should be present in DotCcLib but absent from DotCcExports.
            dotCcLib.GetMethod("helper", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .ShouldNotBeNull();
            exports.GetMethod("helper", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .ShouldBeNull();
        }
        finally
        {
            File.Delete(tempC);
        }
    }

    [Fact]
    public void Lib_mode_does_not_require_main()
    {
        var tempC = Path.GetTempFileName() + ".c";
        File.WriteAllText(tempC, "int foo(int x) { return x + 1; }");
        try
        {
            // Should NOT throw "no `main` function defined" in library mode.
            var program = Compiler.EmitCSharp(
                new[] { tempC },
                includeDirs: null,
                defines: null,
                fileBased: false,
                libraryMode: true);
            program.ShouldContain("EntryPoint = \"foo\"");
        }
        finally
        {
            File.Delete(tempC);
        }
    }

    private static Assembly CompileLibrary(string csharpSource)
    {
        var syntax = CSharpSyntaxTree.ParseText(csharpSource,
            new CSharpParseOptions(LanguageVersion.Preview));

        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();
        // The spliced runtime block references System.Diagnostics.Process
        // (system()); it's type-forwarded and not in the harvested set. See
        // FixtureRunner.AddReferenceByType.
        FixtureRunner.AddReferenceByType(refs, typeof(System.Diagnostics.Process));

        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true,
            optimizationLevel: OptimizationLevel.Debug,
            nullableContextOptions: NullableContextOptions.Disable);

        var comp = CSharpCompilation.Create(
            assemblyName: "DotCCLibTest_" + Guid.NewGuid().ToString("N"),
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
            throw new InvalidOperationException(
                "Roslyn rejected lib-mode emitted C#:\n" + errs + "\n\n--- source ---\n" + csharpSource);
        }
        pe.Position = 0;

        var alc = new AssemblyLoadContext($"dotcc-lib-{Guid.NewGuid():N}", isCollectible: true);
        return alc.LoadFromStream(pe);
    }

    private static void AssertExport(Type exportsType, string name, int expectedParamCount)
    {
        var method = exportsType.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
        method.ShouldNotBeNull($"DotCcExports.{name} should exist");
        method!.GetParameters().Length.ShouldBe(expectedParamCount);

        // UnmanagedCallersOnly is the marker NativeAOT picks up.
        var attr = method.GetCustomAttributes()
            .FirstOrDefault(a => a.GetType().FullName == "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute");
        attr.ShouldNotBeNull($"DotCcExports.{name} should have [UnmanagedCallersOnly]");

        // Verify the EntryPoint property matches the C-source function name.
        var entryPoint = (string?)attr!.GetType()
            .GetField("EntryPoint")!
            .GetValue(attr);
        entryPoint.ShouldBe(name);
    }
}
