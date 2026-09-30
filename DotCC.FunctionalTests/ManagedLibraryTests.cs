#nullable enable

using System;
using System.IO;
using System.Reflection;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// A managed library (<c>-shared -fassembly</c>) compiles to an assembly whose external
/// functions and objects other assemblies reach: the C# accessibility the link gives each
/// name holds in the built assembly, and an object's address, taken inside the library, is
/// the storage of the public field another assembly sees.
/// </summary>
public sealed class ManagedLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dotcc-asmlib-f-{Guid.NewGuid():N}");

    public ManagedLibraryTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Object(string name, string source)
    {
        var src = Path.Combine(_dir, name);
        File.WriteAllText(src, source);
        var obj = Path.ChangeExtension(src, ".o.cs");
        File.WriteAllText(obj, Compiler.EmitObject(src));
        return obj;
    }

    [Fact]
    public unsafe void A_library_built_from_objects_exposes_its_external_names_and_hides_its_static_ones()
    {
        var a = Object("a.c", """
            int counter = 0;
            static int bump(int x) { return x + 1; }
            int add(int x) { counter += bump(x) - 1; return counter; }
            """);
        var b = Object("b.c", """
            static int hidden = 7;
            extern int counter;
            int get_hidden(void) { return hidden; }
            int *counter_addr(void) { return &counter; }
            """);
        var (program, _) = Compiler.LinkAssembly(new[] { a, b }, "mylib");
        var asm = LibraryModeTests.CompileLibrary(program);

        var fns = asm.GetType("DotCcLib_mylib_Program").ShouldNotBeNull();
        var globals = asm.GetType("DotCcLib_mylib_Globals").ShouldNotBeNull();
        fns.IsPublic.ShouldBeTrue();
        globals.IsPublic.ShouldBeTrue();

        var add = fns.GetMethod("add", BindingFlags.Public | BindingFlags.Static).ShouldNotBeNull();
        add.Invoke(null, new object[] { 5 }).ShouldBe(5);
        add.Invoke(null, new object[] { 2 }).ShouldBe(7);
        fns.GetMethod("get_hidden", BindingFlags.Public | BindingFlags.Static).ShouldNotBeNull()
            .Invoke(null, null).ShouldBe(7);

        // The unit's static names are internal: not part of what the library exports.
        foreach (var m in fns.GetMethods(BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (m.Name.StartsWith("bump__", StringComparison.Ordinal)) { m.IsAssembly.ShouldBeTrue(m.Name); }
        }
        globals.GetField("counter", BindingFlags.Public | BindingFlags.Static).ShouldNotBeNull()
            .GetValue(null).ShouldBe(7);
        globals.GetFields(BindingFlags.Public | BindingFlags.Static)
            .ShouldNotContain(f => f.Name.StartsWith("hidden__", StringComparison.Ordinal));

        // `&counter` inside the library is the public field's storage.
        var addr = fns.GetMethod("counter_addr", BindingFlags.Public | BindingFlags.Static).ShouldNotBeNull()
            .Invoke(null, null);
        var p = (int*)Pointer.Unbox(addr.ShouldNotBeNull());
        (*p).ShouldBe(7);
        *p = 40;
        globals.GetField("counter").ShouldNotBeNull().GetValue(null).ShouldBe(40);
    }

    [Fact]
    public void A_program_linked_against_a_managed_library_shares_its_objects_types_and_runtime()
    {
        var libDir = Path.Combine(_dir, "lib");
        Directory.CreateDirectory(libDir);
        var (library, manifest) = Compiler.LinkAssembly(new[]
        {
            Object("a.c", """
                struct point { int x, y; };
                int counter = 0;
                int add(int x) { counter += x; return counter; }
                int area(struct point *p) { return p->x * p->y; }
                """),
            Object("b.c", """
                extern int counter;
                int *counter_addr(void) { return &counter; }
                """),
        }, "mylib");
        File.WriteAllText(Path.Combine(libDir, Compiler.LibraryManifestFile("mylib")), manifest);

        // The program declares what it uses, as a C program does from a library's header.
        var program = Compiler.LinkObjects(new[]
        {
            Object("main.c", """
                #include <stdio.h>
                struct point { int x, y; };
                extern int counter;
                int add(int x);
                int area(struct point *p);
                int *counter_addr(void);
                int main(void) {
                    struct point p = { 3, 4 };
                    add(5);
                    add(2);
                    printf("%d %d %d\n", counter, area(&p), counter_addr() == &counter);
                    return 0;
                }
                """),
        }, EmitMode.Csproj, imports: new ImportOptions(new[] { "mylib" }, new[] { libDir }, Array.Empty<string>()));

        // One load context, where the program's reference to the library resolves.
        var context = new System.Runtime.Loader.AssemblyLoadContext($"dotcc-linked-{Guid.NewGuid():N}", isCollectible: false);
        var (_, reference) = LibraryModeTests.CompileLibrary(library, "mylib", context);
        var (stdout, stderr, exit) = FixtureRunner.CompileAndRunCapturingStreams(program, Array.Empty<string>(), new[] { reference }, context);
        stderr.ShouldBeEmpty();
        exit.ShouldBe(0);
        stdout.ShouldBe("7 12 1\n");
    }

    [Fact]
    public void A_program_dlopens_an_extension_linked_against_its_library_and_calls_into_it()
    {
        var coreDir = Path.Combine(_dir, "core");
        Directory.CreateDirectory(coreDir);
        var (core, coreManifest) = Compiler.LinkAssembly(new[]
        {
            Object("core.c", """
                int counter = 0;
                int bump(int x) { counter += x; return counter; }
                """),
        }, "core");
        File.WriteAllText(Path.Combine(coreDir, Compiler.LibraryManifestFile("core")), coreManifest);
        var linkCore = new ImportOptions(new[] { "core" }, new[] { coreDir }, Array.Empty<string>());

        // An extension module, as CPython's are: its own assembly, linked against the library
        // the program links, and loaded by the program at run time.
        var (extension, _) = Compiler.LinkAssembly(new[]
        {
            Object("ext.c", """
                extern int counter;
                int bump(int x);
                int ext_twice(int x) { return bump(x) * 2; }
                int *ext_counter(void) { return &counter; }
                """),
        }, "ext", linkCore);

        // `twice` and `addr` keep the C convention the direct cast of dlsym gives them;
        // `later` is a variable of the program's own function pointer type, assigned after its
        // declaration, as dynload_shlib.c's `dl_funcptr p` is.
        var program = Compiler.LinkObjects(new[]
        {
            Object("main.c", """
                #include <stdio.h>
                #include <dlfcn.h>
                extern int counter;
                int bump(int x);
                typedef int (*twice_fn)(int);
                int main(int argc, char **argv) {
                    void *h = dlopen(argv[1], RTLD_NOW);
                    if (!h) { printf("dlopen: %s\n", dlerror()); return 1; }
                    int (*twice)(int) = (int (*)(int))dlsym(h, "ext_twice");
                    int *(*addr)(void) = (int *(*)(void))dlsym(h, "ext_counter");
                    twice_fn later;
                    later = (twice_fn)dlsym(h, "ext_twice");
                    bump(1);
                    int a = twice(2);
                    int b = later(3);
                    printf("%d %d %d %d\n", a, b, counter, addr() == &counter);
                    printf("%d %d\n", dlopen(argv[1], RTLD_NOW) == h, dlsym(h, "missing") == NULL);
                    printf("%s\n", dlerror());
                    return dlclose(h);
                }
                """),
        }, EmitMode.Csproj, imports: linkCore);

        var context = new System.Runtime.Loader.AssemblyLoadContext($"dotcc-dlopen-{Guid.NewGuid():N}", isCollectible: false);
        var (_, coreReference) = LibraryModeTests.CompileLibrary(core, "core", context);
        // The loaded extension stays mapped (a load context that is not collectible never
        // unloads), so on Windows its directory outlives Dispose; it is left in the temp dir.
        var extDir = Path.Combine(Path.GetTempPath(), $"dotcc-dlopen-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extDir);
        var extPath = Path.Combine(extDir, "ext.dll");
        File.WriteAllBytes(extPath, LibraryModeTests.CompileImage(extension, "ext", new[] { coreReference }));

        var (stdout, stderr, exit) = FixtureRunner.CompileAndRunCapturingStreams(
            program, new[] { extPath }, new[] { coreReference }, context);
        stderr.ShouldBeEmpty();
        stdout.ShouldBe("6 12 6 1\n1 1\nundefined symbol: missing\n");
        exit.ShouldBe(0);
    }
}
