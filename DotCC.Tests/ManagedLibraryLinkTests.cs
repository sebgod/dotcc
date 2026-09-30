#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// A managed library (<c>-shared -fassembly</c>, <see cref="EmitMode.Assembly"/>) is the .NET
/// counterpart of a C shared library: an assembly other dotcc programs and extension modules
/// link against. Visibility follows linkage (external names public, a unit's <c>static</c>
/// names internal), every type is public, the classes carry the library's name, and the
/// manifest records what the library defines for the link step of a program built against it.
/// </summary>
public sealed class ManagedLibraryLinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dotcc-asmlib-{Guid.NewGuid():N}");

    public ManagedLibraryLinkTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Compile each (file name, source) pair to an object; the object paths.</summary>
    private List<string> Objects(params (string Name, string Source)[] units)
    {
        var objects = new List<string>();
        foreach (var (name, source) in units)
        {
            var src = Path.Combine(_dir, name);
            File.WriteAllText(src, source);
            var obj = Path.ChangeExtension(src, ".o.cs");
            File.WriteAllText(obj, Compiler.EmitObject(src, includeDirs: new[] { _dir }));
            objects.Add(obj);
        }
        return objects;
    }

    private const string LibA = """
        int counter = 0;
        static int bump(int x) { return x + 1; }
        int add(int x) { counter += bump(x) - 1; return counter; }
        struct point { int x, y; };
        enum color { RED, GREEN };
        int area(struct point *p) { return p->x * p->y; }
        """;

    private const string LibB = """
        static int hidden = 7;
        extern int counter;
        int get_hidden(void) { return hidden; }
        int *counter_addr(void) { return &counter; }
        """;

    [Fact]
    public void Access_follows_linkage_and_every_type_is_public()
    {
        var (program, _) = Compiler.LinkAssembly(Objects(("a.c", LibA), ("b.c", LibB)), "mylib");
        program.ShouldContain("public static unsafe class DotCcLib_mylib_Program\n");
        program.ShouldContain("public static unsafe class DotCcLib_mylib_Globals\n");
        program.ShouldContain("public static unsafe int add(int x)");
        program.ShouldContain("public static unsafe int* counter_addr()");
        Regex.IsMatch(program, @"internal static unsafe int bump__a_[0-9a-f]{6}\(int x\)").ShouldBeTrue();
        program.ShouldContain("    public static unsafe int counter = 0;");
        Regex.IsMatch(program, @"    internal static unsafe int hidden__b_[0-9a-f]{6} = 7;").ShouldBeTrue();
        program.ShouldContain("public unsafe struct point\n");
        program.ShouldContain("public enum color : int\n");
    }

    [Fact]
    public void The_library_has_no_entry_point_and_carries_the_runtime()
    {
        var (program, _) = Compiler.LinkAssembly(Objects(("a.c", LibA)), "mylib");
        program.ShouldNotContain("__DotCcEntry");
        program.ShouldNotContain("class DotCcProgram");
        program.ShouldContain("public static unsafe partial class Libc");
        program.ShouldContain("using static DotCcLib_mylib_Globals;");
        program.ShouldContain("using static DotCcLib_mylib_Program;");
    }

    [Fact]
    public void The_manifest_records_the_assembly_its_classes_external_names_and_types()
    {
        var (_, manifest) = Compiler.LinkAssembly(Objects(("a.c", LibA), ("b.c", LibB)), "mylib");
        var lines = manifest.Split('\n');
        lines[0].ShouldStartWith("//!dotcc library 1");
        lines.ShouldContain("//!!dotcc-lib assembly:mylib");
        lines.ShouldContain("//!!dotcc-lib class:DotCcLib_mylib_Globals");
        lines.ShouldContain("//!!dotcc-lib class:DotCcLib_mylib_Program");
        var defs = lines.Where(l => l.StartsWith("//!!dotcc-lib def:", StringComparison.Ordinal))
            .Select(l => l["//!!dotcc-lib def:".Length..]).Order(StringComparer.Ordinal);
        // The unit-local `bump` and `hidden` are not the library's to export.
        defs.ShouldBe(new[] { "add", "area", "counter", "counter_addr", "get_hidden" });
        // Types keep each object's own text, so a program's objects compare against it.
        manifest.ShouldContain("//!!dotcc-obj type:point\nunsafe struct point\n");
        manifest.ShouldContain("//!!dotcc-obj type:color\nenum color : int\n");
    }

    [Fact]
    public void A_struct_only_pointed_at_is_a_public_placeholder_in_the_manifest_too()
    {
        var (program, manifest) = Compiler.LinkAssembly(
            Objects(("a.c", "struct opaque; int use(struct opaque *p) { return p != 0; }")), "mylib");
        program.ShouldContain("public unsafe struct opaque\n");
        manifest.ShouldContain("//!!dotcc-obj opaque:opaque\nunsafe struct opaque\n");
    }

    [Fact]
    public void The_class_names_are_the_assembly_name_as_an_identifier()
    {
        var (program, _) = Compiler.LinkAssembly(Objects(("a.c", LibA)), "python3.13");
        program.ShouldContain("public static unsafe class DotCcLib_python3_13_Program\n");
        program.ShouldContain("public static unsafe class DotCcLib_python3_13_Globals\n");
    }

    [Fact]
    public void Linking_a_library_still_rejects_a_second_definition()
    {
        var ex = Should.Throw<CompileException>(() => Compiler.LinkAssembly(
            Objects(("a.c", "int twice(void) { return 1; }"), ("b.c", "int twice(void) { return 2; }")), "mylib"));
        ex.Message.ShouldContain("multiple definition of 'twice'");
    }

    [Fact]
    public void A_managed_library_is_linked_from_objects_only()
    {
        var src = Path.Combine(_dir, "whole.c");
        File.WriteAllText(src, "int f(void) { return 1; }");
        Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }, emit: EmitMode.Assembly))
            .Message.ShouldContain("--emit=obj");
        Should.Throw<ArgumentException>(() => Compiler.LinkObjects(Objects(("a.c", LibA)), emit: EmitMode.Assembly));
    }

    /// <summary>Link a library from the (file name, source) units and write its manifest into
    /// its own directory; the <c>-l</c>/<c>-L</c> options that link a program against it.</summary>
    private ImportOptions Library(string name, params (string Name, string Source)[] units)
    {
        var (_, manifest) = Compiler.LinkAssembly(Objects(units), name);
        var dir = Path.Combine(_dir, "lib-" + name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Compiler.LibraryManifestFile(name)), manifest);
        return new ImportOptions(new[] { name }, new[] { dir }, Array.Empty<string>());
    }

    private const string Main = """
        struct point { int x, y; };
        extern int counter;
        int add(int x);
        int area(struct point *p);
        int main(void) { struct point p = { 2, 3 }; return add(1) + area(&p) + counter; }
        """;

    [Fact]
    public void A_program_linked_against_a_library_uses_its_types_names_and_runtime()
    {
        var lib = Library("mylib", ("a.c", LibA));
        var program = Compiler.LinkObjects(Objects(("main.c", Main)), EmitMode.Csproj, imports: lib);
        program.ShouldContain("using static DotCcLib_mylib_Globals;");
        program.ShouldContain("using static DotCcLib_mylib_Program;");
        // The library's own type, runtime and names: none of them a second time.
        program.ShouldNotContain("unsafe struct point");
        program.ShouldNotContain("partial class Libc");
        program.ShouldNotContain("static unsafe int add(");
        program.ShouldContain("static unsafe int main()");
        program.ShouldContain("return main();");
    }

    [Fact]
    public void The_manifest_is_found_on_the_library_search_path_like_a_shared_object()
    {
        Library("mylib", ("a.c", LibA));
        var dirs = new[] { Path.Combine(_dir, "nowhere"), Path.Combine(_dir, "lib-mylib") };
        Compiler.ManagedLibraryManifests(new ImportOptions(new[] { "mylib", "m" }, dirs, Array.Empty<string>()))
            .ShouldBe(new[] { Path.Combine(_dir, "lib-mylib", "mylib.dotcc-lib") });
    }

    [Fact]
    public void A_type_defined_differently_from_the_library_is_a_link_error()
    {
        var lib = Library("mylib", ("a.c", LibA));
        var ex = Should.Throw<CompileException>(() => Compiler.LinkObjects(
            Objects(("main.c", "struct point { long x; }; int area(struct point *p); int main(void) { return 0; }")),
            EmitMode.Csproj, imports: lib));
        ex.Message.ShouldContain("type 'point' is defined differently in 'main.o.cs' and library 'mylib'");
    }

    [Fact]
    public void A_second_definition_of_a_library_name_is_a_link_error()
    {
        var lib = Library("mylib", ("a.c", LibA));
        var ex = Should.Throw<CompileException>(() => Compiler.LinkObjects(
            Objects(("main.c", "int add(int x) { return x; } int main(void) { return add(1); }")),
            EmitMode.Csproj, imports: lib));
        ex.Message.ShouldContain("multiple definition of 'add' in 'main.o.cs', also defined in library 'mylib'");
    }

    [Fact]
    public void A_program_cannot_complete_a_struct_the_library_only_points_at()
    {
        var lib = Library("mylib", ("a.c", "struct opaque; int use(struct opaque *p) { return p != 0; }"));
        var ex = Should.Throw<CompileException>(() => Compiler.LinkObjects(
            Objects(("main.c", "struct opaque { int x; }; int use(struct opaque *p); int main(void) { struct opaque o; return use(&o); }")),
            EmitMode.Csproj, imports: lib));
        ex.Message.ShouldContain("type 'opaque' is only declared in library 'mylib', but 'main.o.cs' defines it");
    }

    [Fact]
    public void Linking_against_a_managed_library_needs_a_project()
    {
        var lib = Library("mylib", ("a.c", LibA));
        Should.Throw<CompileException>(() => Compiler.LinkObjects(Objects(("main.c", Main)), EmitMode.File, imports: lib))
            .Message.ShouldContain("--emit=csproj");
    }

    [Fact]
    public void The_program_project_references_the_library_project()
    {
        var csproj = Compiler.BuildGeneratedCsproj(assemblyName: "app", projectReferences: new[] { @"..\lib\mylib.csproj" });
        csproj.ShouldContain("<ProjectReference Include=\"../lib/mylib.csproj\" />");
        csproj.ShouldContain("<OutputType>Exe</OutputType>");
    }

    [Fact]
    public void The_managed_library_project_is_a_plain_class_library()
    {
        var csproj = Compiler.BuildGeneratedCsproj(libraryMode: true, assemblyName: "mylib", managedLibrary: true);
        csproj.ShouldContain("<OutputType>Library</OutputType>");
        csproj.ShouldContain("<AssemblyName>mylib</AssemblyName>");
        csproj.ShouldNotContain("PublishAot");
        csproj.ShouldNotContain("NativeLib");
    }
}
