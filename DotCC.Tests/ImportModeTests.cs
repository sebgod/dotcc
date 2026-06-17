#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Import mode (<c>-l&lt;name&gt;</c> / <c>-L&lt;dir&gt;</c>): a prototype that resolves
/// against a prebuilt native library rather than the managed runtime. dotcc emits a
/// <c>DotCcImports</c> GOT-style function-pointer table (one
/// <c>delegate* unmanaged[Cdecl]&lt;…&gt;</c> field per import) bound at startup. These
/// tests assert the EMISSION SHAPE + the V1 scope-cut warnings; the end-to-end "binds and
/// calls a real .so" path is the opt-in native oracle. The discriminator between a
/// runtime-provided libc proto and a user import is the synthetic line band
/// (<see cref="DotCC.Ir.SrcPos.SyntheticLineBase"/>).
/// </summary>
[Collection("Console")]
public sealed class ImportModeTests
{
    /// <summary>
    /// Emit <paramref name="mainC"/> (with an optional user header in the same dir,
    /// reachable via quoted include + auto-added input dir), capturing stderr so the
    /// scope-cut warnings can be asserted. The unit assembly is serialized
    /// (see AssemblyInfo), so the process-global <c>Console.Error</c> swap is race-free.
    /// </summary>
    private static (string Emit, string Stderr) EmitWithImports(
        string mainC, ImportOptions imports, string? headerName = null, string? headerBody = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-im-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var mainPath = Path.Combine(dir, "main.c");
            File.WriteAllText(mainPath, mainC);
            if (headerName is not null) { File.WriteAllText(Path.Combine(dir, headerName), headerBody ?? ""); }

            var prior = Console.Error;
            var sw = new StringWriter();
            Console.SetError(sw);
            try
            {
                var emit = Compiler.EmitCSharp(new[] { mainPath }, includeDirs: new[] { dir }, imports: imports);
                return (emit, sw.ToString());
            }
            finally { Console.SetError(prior); }
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    private static ImportOptions Dynamic(params string[] names)
        => new(names, Array.Empty<string>(), Array.Empty<string>());

    // ---- render guard (synthetic line band masking) ------------------------

    [Fact]
    public void describe_line_masks_the_synthetic_band()
    {
        // A user line renders raw; a band line never leaks its 1048576-based number.
        Ir.SrcPos.DescribeLine(42).ShouldBe("42");
        Ir.SrcPos.DescribeLine(Ir.SrcPos.SyntheticLineBase).ShouldBe("<system header>:1");
        new Ir.SrcPos(Ir.SrcPos.SyntheticLineBase + 4, 7).ToString().ShouldBe("<system header>:5:7");
        new Ir.SrcPos(12, 3).ToString().ShouldBe("12:3");
    }

    // ---- baseline: no -l changes nothing ----------------------------------

    [Fact]
    public void no_imports_does_not_emit_the_imports_table()
    {
        var (emit, _) = EmitWithImports(
            "#include \"blob.h\"\nint main(void){ return blob_size(3); }",
            ImportOptions.Empty, "blob.h", "int blob_size(int n);");
        // (The string "DotCcImports" also appears in the always-spliced NativeImports
        // doc comment, so assert on the actual class declaration, not the bare word.)
        emit.ShouldNotContain("class DotCcImports");
    }

    // ---- dynamic import: GOT-style fn-ptr table ---------------------------

    [Fact]
    public void dynamic_import_emits_native_fnptr_field_and_binding()
    {
        var (emit, _) = EmitWithImports(
            "#include \"blob.h\"\nint main(void){ return blob_size(3); }",
            Dynamic("blobby"), "blob.h", "int blob_size(int n);");

        // GOT table + the field, rendered with the C calling convention.
        emit.ShouldContain("static unsafe class DotCcImports");
        emit.ShouldContain("delegate* unmanaged[Cdecl]<int, int> blob_size");
        // Surfaced by bare name, bound before main from the named library.
        emit.ShouldContain("using static DotCcImports;");
        emit.ShouldContain("DotCcImports.__BindAll();");
        emit.ShouldContain("NativeImports.LoadLibrary(\"blobby\", __dirs)");
        emit.ShouldContain("TryResolveExport(__libs, \"blob_size\", out __p)");
    }

    [Fact]
    public void library_dirs_are_threaded_into_the_search_path()
    {
        var imports = new ImportOptions(new[] { "blobby" }, new[] { "/opt/blob/lib" }, Array.Empty<string>());
        var (emit, _) = EmitWithImports(
            "#include \"blob.h\"\nint main(void){ return blob_size(3); }",
            imports, "blob.h", "int blob_size(int n);");
        emit.ShouldContain("\"/opt/blob/lib\"");
    }

    // ---- the line-band discriminator: libc protos stay runtime-provided ---

    [Fact]
    public void system_header_proto_is_never_imported()
    {
        // strlen is declared in the synthetic <string.h> (line band) → runtime-provided,
        // never an import candidate even though it is proto-only + referenced.
        var (emit, _) = EmitWithImports(
            "#include <string.h>\nint main(void){ return (int)strlen(\"hi\"); }",
            Dynamic("foo"));
        emit.ShouldNotContain("class DotCcImports");
        emit.ShouldNotContain("\"strlen\", out __p");
    }

    [Fact]
    public void user_proto_is_imported_while_system_proto_is_not()
    {
        var (emit, _) = EmitWithImports(
            "#include <string.h>\n#include \"blob.h\"\n"
            + "int main(void){ return (int)strlen(\"hi\") + blob_size(2); }",
            Dynamic("blobby"), "blob.h", "int blob_size(int n);");
        emit.ShouldContain("delegate* unmanaged[Cdecl]<int, int> blob_size");
        // strlen resolves to the managed runtime, so it must NOT get an import binding.
        emit.ShouldNotContain("\"strlen\", out __p");
    }

    // ---- V1 scope cuts: variadic + extern data -----------------------------

    [Fact]
    public void variadic_import_candidate_warns_and_is_skipped()
    {
        var (emit, stderr) = EmitWithImports(
            "#include \"log.h\"\nint main(void){ return my_log(\"x\"); }",
            Dynamic("foo"), "log.h", "int my_log(const char* fmt, ...);");
        stderr.ShouldContain("cannot import variadic function 'my_log'");
        emit.ShouldNotContain("my_log;"); // no fn-ptr field for it
    }

    [Fact]
    public void extern_data_import_warns_and_is_unsupported()
    {
        var (_, stderr) = EmitWithImports(
            "extern int foo_verbosity;\nint main(void){ return foo_verbosity; }",
            Dynamic("foo"));
        stderr.ShouldContain("extern data import 'foo_verbosity' is not supported");
    }

    // ---- static archives (.a/.lib): DllImport stubs + csproj items ---------

    private static ImportOptions Static(params string[] archivePaths)
        => new(Array.Empty<string>(), Array.Empty<string>(), archivePaths);

    [Fact]
    public void static_archive_emits_dllimport_extern_stub()
    {
        var (emit, _) = EmitWithImports(
            "#include \"blob.h\"\nint main(void){ return blob_size(3); }",
            Static("/opt/libblob.a"), "blob.h", "int blob_size(int n);");
        emit.ShouldContain("static unsafe class DotCcStaticImports");
        emit.ShouldContain("DllImport(\"blob\"");              // libblob.a → "blob"
        emit.ShouldContain("EntryPoint = \"blob_size\"");
        emit.ShouldContain("internal static extern int blob_size(");
        emit.ShouldContain("using static DotCcStaticImports;");
        emit.ShouldNotContain("__BindAll");                    // static binds at the native link
    }

    [Fact]
    public void static_archive_csproj_has_directpinvoke_and_nativelibrary()
    {
        var csproj = Compiler.BuildGeneratedCsproj(
            libraryMode: false, assemblyName: "app", staticArchives: new[] { "/opt/libblob.a" });
        csproj.ShouldContain("<PublishAot>true</PublishAot>");
        csproj.ShouldContain("<DirectPInvoke Include=\"blob\" />");
        csproj.ShouldContain("<NativeLibrary Include=\"/opt/libblob.a\" />");
    }

    [Fact]
    public void mixing_static_and_dynamic_imports_throws()
    {
        var mixed = new ImportOptions(new[] { "foo" }, Array.Empty<string>(), new[] { "/opt/libblob.a" });
        Should.Throw<CompileException>(() => EmitWithImports(
            "#include \"blob.h\"\nint main(void){ return blob_size(3); }",
            mixed, "blob.h", "int blob_size(int n);"));
    }

    // ---- separate compilation: import/def markers + link-time resolution ----

    /// <summary>Emit one TU as an object fragment (with an optional user header alongside).</summary>
    private static string EmitObjFragment(string tuC, string? headerName = null, string? headerBody = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-im-obj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var tuPath = Path.Combine(dir, "tu.c");
            File.WriteAllText(tuPath, tuC);
            if (headerName is not null) { File.WriteAllText(Path.Combine(dir, headerName), headerBody ?? ""); }
            return Compiler.EmitObject(tuPath, includeDirs: new[] { dir });
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>Write fragments to <c>.cs</c> files and link them with the given imports.</summary>
    private static string Link(ImportOptions imports, params string[] fragments)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-im-link-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var paths = new List<string>();
            for (var i = 0; i < fragments.Length; i++)
            {
                var p = Path.Combine(dir, $"obj{i}.cs");
                File.WriteAllText(p, fragments[i]);
                paths.Add(p);
            }
            return Compiler.LinkObjects(paths, imports: imports);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public void obj_fragment_carries_import_and_def_markers()
    {
        var frag = EmitObjFragment(
            "#include \"blob.h\"\nint main(void){ return blob_size(3); }", "blob.h", "int blob_size(int n);");
        // No -l is known at obj time, so the fragment records the candidate + its
        // fn-ptr type and the names this TU defines, for the link step to resolve.
        frag.ShouldContain("//!!dotcc-obj import:blob_size delegate* unmanaged[Cdecl]<int, int>");
        frag.ShouldContain("//!!dotcc-obj def:main");
    }

    [Fact]
    public void link_binds_an_unresolved_import_when_l_is_given()
    {
        var frag = EmitObjFragment(
            "#include \"blob.h\"\nint main(void){ return blob_size(3); }", "blob.h", "int blob_size(int n);");
        var linked = Link(Dynamic("blobby"), frag);
        linked.ShouldContain("delegate* unmanaged[Cdecl]<int, int> blob_size");
        linked.ShouldContain("NativeImports.LoadLibrary(\"blobby\", __dirs)");
    }

    [Fact]
    public void link_definition_wins_over_import()
    {
        // One fragment calls blob_size (proto-only → import candidate); another DEFINES
        // it. The definition resolves the name internally — no import emitted.
        var consumer = EmitObjFragment(
            "#include \"blob.h\"\nint main(void){ return blob_size(3); }", "blob.h", "int blob_size(int n);");
        var definer = EmitObjFragment("int blob_size(int n){ return n + 1; }");
        var linked = Link(Dynamic("blobby"), consumer, definer);
        linked.ShouldNotContain("class DotCcImports");
    }
}
