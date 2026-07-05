#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>Native-library IMPORT MODE rendering (-l/-L/.a): candidate selection and
/// the emitted GOT-style DotCcImports table / static [DllImport] stubs. One concern
/// of <see cref="Compiler"/> — entry points live in the main file.</summary>
public static partial class Compiler
{
    /// <summary>
    /// Select the functions an import (<c>-l</c>) compile must bind against, emitting the
    /// V1 scope-cut warnings to stderr (variadic / global-name collision / extern data).
    /// Candidates are <see cref="Ir.IrBuilder.ProtoOnlyReferenced"/> (proto-only, called,
    /// not from a synthetic header) minus those cuts. Sorted by name for deterministic emit.
    /// </summary>
    private static List<Ir.Symbol> ComputeImportCandidates(Ir.IrBuilder ir, ImportOptions imports)
    {
        var globalNames = new HashSet<string>(ir.Globals.Select(g => g.Sym.Name), StringComparer.Ordinal);
        var result = new List<Ir.Symbol>();
        foreach (var sym in ir.ProtoOnlyReferenced.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            if (sym.Type is Ir.CType.Func { Variadic: true })
            {
                // A varargs signature has no fixed function-pointer form (same reason
                // -shared can't export one), so it can't be a GOT field — warn + skip.
                Console.Error.WriteLine(
                    $"dotcc: warning: cannot import variadic function '{sym.Name}' (no fixed function-pointer signature) — skipped");
                continue;
            }
            if (globalNames.Contains(sym.Name))
            {
                // A defined C global owns the name in the emitted program (C-shaped:
                // the definition wins). Importing the same name would clash — skip it.
                Console.Error.WriteLine(
                    $"dotcc: warning: import candidate '{sym.Name}' collides with a defined global — skipped (the definition wins)");
                continue;
            }
            result.Add(sym);
        }
        foreach (var name in ir.ExternDataReferenced)
        {
            Console.Error.WriteLine(
                $"dotcc: warning: extern data import '{name}' is not supported (import mode binds functions only)");
        }
        return result;
    }

    /// <summary>
    /// Render the <c>DotCcImports</c> GOT-style table: one function-pointer field per
    /// candidate, named exactly like the C function so <c>using static DotCcImports</c>
    /// surfaces it and existing call sites need no change. <c>__BindAll()</c> loads each
    /// <c>-l</c> library (ld.so order) and binds each field to the FIRST library that
    /// exports the symbol, throwing a clean <see cref="DllNotFoundException"/> on a miss.
    /// In <paramref name="libraryMode"/> (<c>-shared</c>, no entry point) a static
    /// constructor runs <c>__BindAll()</c> on first field touch; otherwise the exe shell
    /// calls it from <c>__DotCcEntry()</c> before <c>main</c>. Fields render as
    /// <c>delegate* unmanaged[Cdecl]&lt;…&gt;</c> via the native-call-conv marker, so the
    /// invocation uses the C calling convention (same lowering as a directly-cast
    /// <c>dlsym</c> result — see <c>CType.Func.IsNativeCallConv</c>).
    /// </summary>
    /// <summary>Map import-candidate symbols to <c>(C name, C# fn-ptr field type)</c> pairs —
    /// the native-calling-convention <c>delegate* unmanaged[Cdecl]&lt;…&gt;</c> spelling. Sorted
    /// by name for deterministic emit. This is the form <see cref="RenderImportsClass"/> consumes,
    /// so the same renderer serves whole-program emit (from the IR) AND link-from-markers (from
    /// <c>--emit=obj</c> fragments, where the IR is long gone — see <see cref="LinkObjects"/>).</summary>
    private static List<(string Name, string FieldType)> ImportFieldSpecs(IReadOnlyList<Ir.Symbol> candidates)
    {
        var target = new Backends.CSharpTarget();
        return candidates
            .Select(s => (s.Name, target.RenderType(((Ir.CType.Func)s.Type) with { IsNativeCallConv = true })))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Render the <c>DotCcImports</c> GOT-style table from <c>(C name, fn-ptr field type)</c>
    /// pairs: one field per candidate, named exactly like the C function so
    /// <c>using static DotCcImports</c> surfaces it and existing call sites need no change.
    /// <c>__BindAll()</c> loads each <c>-l</c> library (ld.so order) and binds each field to the
    /// FIRST library that exports the symbol, throwing a clean <see cref="DllNotFoundException"/>
    /// on a miss. In <paramref name="libraryMode"/> (<c>-shared</c>, no entry point) a static
    /// constructor runs <c>__BindAll()</c> on first field touch; otherwise the exe shell calls it
    /// from <c>__DotCcEntry()</c> before <c>main</c>. The field type is already the
    /// <c>delegate* unmanaged[Cdecl]&lt;…&gt;</c> spelling (see <c>CType.Func.IsNativeCallConv</c>).
    /// </summary>
    private static string RenderImportsClass(
        IReadOnlyList<(string Name, string FieldType)> fields, ImportOptions imports, bool libraryMode)
    {
        var sb = new StringBuilder();
        sb.Append("// ---- native imports (`-l`) — GOT-style function-pointer table ----\n");
        sb.Append("// Each field is bound once by __BindAll() (before main, or on first touch\n");
        sb.Append("// in -shared mode) to the address the named symbol resolves to in the -l\n");
        sb.Append("// libraries, searched in order — first that exports it wins (ld.so model).\n");
        sb.Append("static unsafe class DotCcImports\n{\n");
        foreach (var (name, ft) in fields)
        {
            sb.Append($"    internal static {ft} {EmitHelpers.Id(name)};\n");
        }
        sb.Append('\n');
        if (libraryMode)
        {
            sb.Append("    static DotCcImports() => __BindAll(); // no entry point in -shared; bind on first field touch\n\n");
        }
        sb.Append("    internal static void __BindAll()\n    {\n");
        sb.Append("        var __dirs = new string[] { ");
        sb.Append(string.Join(", ", imports.LibraryDirs.Select(CsStringLiteral)));
        sb.Append(" };\n");
        sb.Append("        var __libs = new System.IntPtr[]\n        {\n");
        foreach (var lib in imports.LinkLibraries)
        {
            sb.Append($"            NativeImports.LoadLibrary({CsStringLiteral(lib)}, __dirs),\n");
        }
        sb.Append("        };\n");
        var libList = string.Join(", ", imports.LinkLibraries);
        sb.Append("        void* __p;\n");
        foreach (var (name, ft) in fields)
        {
            // The NATIVE symbol looked up is the raw C name; the C# field name is the
            // keyword-escaped form the call sites also emit (so they bind to this field).
            sb.Append($"        if (!NativeImports.TryResolveExport(__libs, {CsStringLiteral(name)}, out __p))\n");
            sb.Append($"            throw new System.DllNotFoundException(\"dotcc: undefined symbol '{name}' — not exported by any of: {libList}\");\n");
            sb.Append($"        {EmitHelpers.Id(name)} = ({ft})__p;\n");
        }
        sb.Append("    }\n}\n");
        return sb.ToString();
    }

    /// <summary>Emit a C# string literal for an arbitrary path/name — escapes backslashes
    /// (Windows <c>-L</c> paths) and double-quotes. Plain identifiers pass through unchanged.</summary>
    private static string CsStringLiteral(string s)
        => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>The <c>[DllImport]</c> / <c>&lt;DirectPInvoke&gt;</c> library name for a static
    /// archive path: the file name minus extension and the Unix <c>lib</c> prefix
    /// (<c>/x/libmylib.a</c> → <c>mylib</c>, <c>mylib.lib</c> → <c>mylib</c>).</summary>
    private static string StaticArchiveDllName(string archivePath)
    {
        var name = Path.GetFileNameWithoutExtension(archivePath);
        return name.StartsWith("lib", StringComparison.Ordinal) ? name["lib".Length..] : name;
    }

    /// <summary>
    /// Render <c>DotCcStaticImports</c>: a classic <c>[DllImport]</c> extern stub per
    /// candidate (named like the C function — <c>using static</c> surfaces it, so call
    /// sites are unchanged), resolved at NativeAOT publish against the linked archives
    /// (the generated csproj's <c>&lt;DirectPInvoke&gt;</c>/<c>&lt;NativeLibrary&gt;</c> items).
    /// <c>[DllImport]</c> not <c>[LibraryImport]</c>: no source generator, so it survives
    /// the generator-less Roslyn compile in tests and matches DotCC.Libc's own pattern.
    /// All stubs carry one conventional lib name — with multiple archives the native
    /// linker resolves symbols across all of them regardless of which name a stub names.
    /// <c>dotnet run</c> throws <see cref="DllNotFoundException"/> (inherent — static
    /// linking needs <c>dotnet publish -r &lt;RID&gt;</c>).
    /// </summary>
    private static string RenderStaticImportsClass(IReadOnlyList<Ir.Symbol> candidates, ImportOptions imports)
    {
        var target = new Backends.CSharpTarget();
        var libName = StaticArchiveDllName(imports.StaticArchives[0]);
        const string dll = "System.Runtime.InteropServices.DllImport";
        const string cdecl = "System.Runtime.InteropServices.CallingConvention.Cdecl";
        var sb = new StringBuilder();
        sb.Append("// ---- native imports (static .a/.lib) — DirectPInvoke extern stubs ----\n");
        sb.Append("// Resolved at NativeAOT publish against the linked archives (the csproj's\n");
        sb.Append("// <DirectPInvoke>/<NativeLibrary> items). `dotnet run` throws DllNotFound —\n");
        sb.Append("// these need `dotnet publish -c Release -r <RID>`.\n");
        sb.Append("static unsafe class DotCcStaticImports\n{\n");
        foreach (var sym in candidates.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var fn = (Ir.CType.Func)sym.Type;
            var ret = target.RenderType(fn.Return);
            var ps = string.Join(", ", fn.Params.Select((p, i) => $"{target.RenderType(p)} _p{i}"));
            // EntryPoint = the raw C symbol; the C# method name is the keyword-escaped
            // form the call sites emit (so a bare call binds to this extern).
            sb.Append($"    [{dll}({CsStringLiteral(libName)}, EntryPoint = {CsStringLiteral(sym.Name)}, ExactSpelling = true, CallingConvention = {cdecl})]\n");
            sb.Append($"    internal static extern {ret} {EmitHelpers.Id(sym.Name)}({ps});\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

}
