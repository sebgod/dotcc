#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>
/// Native-library import options for dotcc IMPORT MODE — the clang-shaped
/// <c>-l&lt;name&gt;</c> / <c>-L&lt;dir&gt;</c> linking surface.
/// <see cref="LinkLibraries"/> are dynamic libraries bound GOT-style at startup
/// (the emitted <c>DotCcImports</c> function-pointer table); <see cref="LibraryDirs"/>
/// are the <c>-L</c> search dirs; <see cref="StaticArchives"/> are <c>.a</c>/<c>.lib</c>
/// inputs linked at NativeAOT publish. Empty by default — import mode is entirely opt-in,
/// so a compile with no <c>-l</c> is byte-identical to before.
/// </summary>
public sealed record ImportOptions(
    IReadOnlyList<string> LinkLibraries,
    IReadOnlyList<string> LibraryDirs,
    IReadOnlyList<string> StaticArchives)
{
    /// <summary>The no-imports default.</summary>
    public static readonly ImportOptions Empty =
        new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    /// <summary>True when any dynamic (<c>-l</c>) or static (<c>.a</c>/<c>.lib</c>) import was requested.</summary>
    public bool HasAny => LinkLibraries.Count > 0 || StaticArchives.Count > 0;
}

/// <summary>
/// Public compiler API. Two top-level entry points:
/// <list type="bullet">
///   <item><see cref="EmitCSharp"/> — compile one or more <c>.c</c> translation
///     units to a single C# source string. Pass <c>fileBased: true</c> for a
///     .NET 10 file-based program (with the <c>#:property AllowUnsafeBlocks</c>
///     header); <c>fileBased: false</c> for the csproj-paired shell.</item>
///   <item><see cref="Preprocess"/> — run the preprocessor only (-E mode) and
///     write the post-expansion token stream to a <see cref="TextWriter"/>.
///     Useful from tests to assert <c>#include</c> / <c>#define</c> behavior
///     in isolation from parsing.</item>
/// </list>
/// Both throw <see cref="CompileException"/> with a human-readable message on
/// parse error / missing <c>main</c>. Pure (no <c>Console</c>, no
/// <see cref="System.Diagnostics.Process"/>) so the frontend exe AND the
/// test project drive them the same way.
/// </summary>
public static class Compiler
{
    /// <summary>
    /// Synthetic system headers — resolved by <see cref="CPreprocessor.OnInclude"/>
    /// alongside any user <c>.h</c> files found on the include path. User
    /// headers win on name collisions (mirrors clang's local-first rule for
    /// quoted includes).
    /// </summary>
    /// <remarks>
    /// Source: real <c>.h</c> files under <c>DotCC.Lib/include/</c>, embedded
    /// into this assembly as resources at build time (manifest names of the
    /// form <c>DotCC.SystemHeaders.&lt;filename&gt;</c>) — same shape as
    /// clang's <c>lib/clang/&lt;ver&gt;/include/</c> tree, just loaded from
    /// the assembly manifest instead of disk. Edit the file, rebuild, and
    /// the new content is picked up. Lazy-initialized so the loader cost
    /// hits the first <c>EmitCSharp</c>/<c>Preprocess</c> call rather than
    /// every type-init.
    /// </remarks>
    internal static IReadOnlyDictionary<string, string> SystemHeaders => _systemHeaders.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> _systemHeaders =
        new(LoadEmbeddedSystemHeaders);

    private static Dictionary<string, string> LoadEmbeddedSystemHeaders()
    {
        const string prefix = "DotCC.SystemHeaders.";
        var asm = typeof(Compiler).Assembly;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) { continue; }
            var fileName = name[prefix.Length..];
            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"missing embedded header resource: {name}");
            using var reader = new StreamReader(stream);
            // Splice line continuations here so the synthetic headers (some of
            // which use multi-line macros) lex like any other source.
            map[fileName] = SpliceLineContinuations(reader.ReadToEnd());
        }
        return map;
    }

    /// <summary>
    /// True iff <paramref name="content"/> is the body of one of dotcc's synthetic
    /// system headers — NOT a user <c>-I</c> header that happens to share the name.
    /// The include map seeds <see cref="SystemHeaders"/>'s string REFERENCES and a
    /// user header on the path overwrites the slot with a freshly-read string, so
    /// reference identity distinguishes the embedded original from a shadowing copy.
    /// <see cref="CPreprocessor.OnInclude"/> uses this to lex synthetic headers in the
    /// reserved line band (see <see cref="Ir.SrcPos.SyntheticLineBase"/>), which is what
    /// flags every prototype they declare as runtime-provided rather than importable.
    /// </summary>
    internal static bool IsSyntheticHeaderContent(string name, string content)
        => SystemHeaders.TryGetValue(name, out var sys) && ReferenceEquals(sys, content);

    /// <summary>
    /// Concatenated DotCC.Libc runtime source — the embedded <c>.cs</c>
    /// files from <c>../DotCC.Libc/*.cs</c> with their file-scope
    /// artifacts (<c>#nullable enable</c>, <c>using</c> directives,
    /// <c>namespace DotCC.Libc;</c>) stripped so the contained class +
    /// struct declarations land cleanly inside the emitted file's
    /// type-declarations section. <see cref="BuildShell"/> splices this
    /// block in once per emit. Single source of truth: editing
    /// <c>../DotCC.Libc/Libc.cs</c> updates BOTH the unit-tested DLL
    /// AND every emitted program.
    /// </summary>
    private static readonly Lazy<string> _runtimeBlock = new(LoadRuntimeBlock);

    private static string LoadRuntimeBlock()
    {
        const string prefix = "DotCC.Runtime.";
        var asm = typeof(Compiler).Assembly;
        var pieces = new List<(string FileName, string Content)>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) { continue; }
            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"missing embedded runtime resource: {name}");
            using var reader = new StreamReader(stream);
            pieces.Add((name[prefix.Length..], reader.ReadToEnd()));
        }
        // Deterministic order — emitted code should be byte-identical
        // across runs given the same inputs.
        pieces.Sort((a, b) => StringComparer.Ordinal.Compare(a.FileName, b.FileName));

        var sb = new StringBuilder();
        sb.AppendLine("// ---- Embedded DotCC.Libc runtime — single source of truth.");
        sb.AppendLine("//      Edits to ../DotCC.Libc/*.cs land here automatically on next build.");
        foreach (var (fileName, content) in pieces)
        {
            sb.AppendLine($"// ---- {fileName} ----");
            // The DotCC.Libc sources are all `#nullable enable`, but
            // StripFileScopeArtifacts removed that file-scope directive (with
            // the usings/namespace) so the types concatenate cleanly into the
            // emitted file's type-decls region. Re-establish a per-file
            // nullable context with an explicit enable/restore pair (push/pop):
            // the original `?`-annotated signatures keep their meaning and the
            // emitted program compiles warning-free even though its project
            // sets <Nullable>disable</Nullable> (without this, every annotation
            // in the runtime trips CS8669 — "nullable annotation outside a
            // #nullable context"). `restore` (not `disable`) is the pop — it
            // returns the context to that project default.
            sb.AppendLine("#nullable enable");
            sb.AppendLine(StripFileScopeArtifacts(content));
            sb.AppendLine("#nullable restore");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Remove file-scope artifacts from a source string so the remaining
    /// type declarations can be concatenated into a different file's
    /// type-declaration region. Stripped:
    /// <list type="bullet">
    ///   <item><c>#nullable enable</c> / <c>#nullable disable</c></item>
    ///   <item>top-level <c>using</c> directives (the shell already
    ///     declares the union it needs)</item>
    ///   <item><c>namespace DotCC.Libc;</c> (so the contained classes
    ///     land at file scope in the emitted program)</item>
    /// </list>
    /// </summary>
    private static string StripFileScopeArtifacts(string src)
    {
        var sb = new StringBuilder(src.Length);
        foreach (var rawLine in src.Split('\n'))
        {
            var trimmed = rawLine.TrimStart();
            if (trimmed.StartsWith("#nullable", StringComparison.Ordinal)) { continue; }
            // Strip file-scope `using` DIRECTIVES only — these sit at column 0.
            // An indented `using ` is a statement (`using var x = …;` /
            // `using (…)`) inside a method body and must be kept, so match the
            // raw (un-trimmed) line, not the trimmed one.
            if (rawLine.StartsWith("using ", StringComparison.Ordinal)) { continue; }
            if (trimmed.StartsWith("namespace ", StringComparison.Ordinal)) { continue; }
            sb.Append(rawLine).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// C#-side type names that the parser should recognise as type
    /// identifiers from the start — without first seeing them on the
    /// LHS of a <c>typedef</c>. Used to expose opaque libc-class types
    /// (mainly through synthetic headers like <c>&lt;setjmp.h&gt;</c>
    /// which writes <c>typedef LongJmpToken jmp_buf;</c>). dotcc
    /// deliberately avoids inventing new C keywords for these — the
    /// grammar stays a pure C subset, and the seed mechanism here is
    /// what lets typedef-chain a C-side alias to a C#-side class.
    /// </summary>
    internal static readonly string[] PredefinedTypeNames =
    {
        "LongJmpToken", // <setjmp.h> — opaque jmp_buf target
        "VaList",       // <stdarg.h> — va_list cursor (Libc.VaList value type)
        "thrd_t",       // <threads.h> — opaque thread handle (Libc.thrd_t struct)
        "mtx_t",        // <threads.h> — opaque mutex handle (Libc.mtx_t struct)
        "div_t",        // <stdlib.h> — div() result (Libc.div_t struct)
        "ldiv_t",       // <stdlib.h> — ldiv() result (Libc.ldiv_t struct)
        "lldiv_t",      // <stdlib.h> — lldiv() result (Libc.lldiv_t struct)
        "imaxdiv_t",    // <inttypes.h> — imaxdiv() result (Libc.imaxdiv_t struct)
        "FILE",         // <stdio.h> — opaque stream handle; FILE* stays a real
                        // pointer-to-struct (Libc.FILE), so NULL/==/if(fp) all
                        // work through the normal pointer machinery.
        "char16_t",     // <uchar.h> — C11 UTF-16 code unit. Unlike the names above
                        // (which resolve to a verbatim Libc type), char16_t is
                        // pre-seeded in IrBuilder._typedefs to CType.Char16 → C# char.
        "wchar_t",      // <wchar.h> — likewise pre-seeded in IrBuilder._typedefs to
                        // CType.WChar → C# char (dotcc's MSVC-shaped 16-bit wchar_t).
    };

    /// <summary>
    /// The C front-end behind the <see cref="Frontends.IFrontend"/> seam, kept as a
    /// thin shim so the existing call sites stay unchanged. The resulting
    /// <see cref="Ir.IrBuilder"/> is consumed by <see cref="EmitCSharp"/> (C#) and the
    /// wat path alike — one front-end, many targets.
    /// </summary>
    private static Ir.IrBuilder BuildIr(
        IReadOnlyList<string> inputPaths,
        IReadOnlyList<string>? includeDirs,
        IReadOnlyList<string>? defines,
        CDialect? dialect,
        bool pedantic,
        bool pedanticErrors,
        Ir.INameLegalizer? names = null,
        bool warnDiscardedQualifiers = true)
        => new Frontends.CFrontend().BuildIr(new Frontends.FrontendRequest(
            inputPaths, includeDirs, defines, dialect, pedantic, pedanticErrors, names, warnDiscardedQualifiers));

    /// <summary>
    /// Compile <paramref name="inputPaths"/> to a single C# source string.
    /// </summary>
    /// <param name="libraryMode">When true (frontend's <c>-shared</c> flag),
    /// emit a NativeAOT-publishable shared-library shell: user functions
    /// live in <c>internal static class DotCcLib</c>, and non-static C
    /// functions get a matching <c>[UnmanagedCallersOnly]</c> wrapper in
    /// <c>public static class DotCcExports</c>. Otherwise emit the
    /// standalone-executable shell with a <c>return main(…);</c> entry.</param>
    public static string EmitCSharp(
        IReadOnlyList<string> inputPaths,
        IReadOnlyList<string>? includeDirs = null,
        IReadOnlyList<string>? defines = null,
        bool fileBased = true,
        bool libraryMode = false,
        CDialect? dialect = null,
        bool pedantic = false,
        bool pedanticErrors = false,
        bool asObject = false,
        bool warnConversion = false,
        bool debugHeap = false,
        ImportOptions? imports = null,
        bool warnDiscardedQualifiers = true)
    {
        var irBuilder = BuildIr(inputPaths, includeDirs, defines, dialect, pedantic, pedanticErrors,
            warnDiscardedQualifiers: warnDiscardedQualifiers);
        // -Wconversion: collect narrowing-conversion warnings during codegen, then
        // flush to stderr. Off by default (no gate → no checks).
        var convGate = warnConversion ? new ConversionGate() : null;
        var cg = Backends.CSharpBackend.Run(irBuilder, convGate);
        if (convGate is { HasAny: true })
        {
            foreach (var d in convGate.Diagnostics) { Console.Error.WriteLine("dotcc: warning: " + d); }
        }
        // Import mode: bind prototypes that resolve against a native library rather
        // than the managed runtime. Dynamic (-l) → a GOT-style DotCcImports table bound
        // at startup; static (.a/.lib) → [DllImport] extern stubs resolved at NativeAOT
        // publish. Object fragments serialize markers at link time instead (separate path).
        var importsClass = "";
        var importsAreStatic = false;
        if (!asObject && imports is not null && imports.HasAny)
        {
            if (imports.LinkLibraries.Count > 0 && imports.StaticArchives.Count > 0)
            {
                // Attribution across the static/dynamic boundary is unknowable without
                // reading the archives — reject loudly rather than mis-bind (V1).
                throw new CompileException(
                    "mixing static archives (.a/.lib) and -l dynamic libraries in one compile is not yet supported");
            }
            importsAreStatic = imports.StaticArchives.Count > 0;
            var candidates = ComputeImportCandidates(irBuilder, imports);
            if (candidates.Count > 0)
            {
                importsClass = importsAreStatic
                    ? RenderStaticImportsClass(candidates, imports)
                    : RenderImportsClass(ImportFieldSpecs(candidates), imports, libraryMode);
            }
        }
        // Library mode doesn't need a `main` (the .dll is consumed through its
        // exports); object mode links later. Exe mode requires an entry point.
        if (!asObject && !libraryMode && cg.MainArity < 0)
        {
            throw new CompileException("no `main` function defined in any translation unit.");
        }
        if (asObject)
        {
            // Serialize THIS TU's import candidates (non-variadic ProtoOnlyReferenced —
            // no `-l` is known yet, so no warnings/collision filtering; the link step
            // decides) and the names it defines (functions + globals), so the linker
            // can resolve cross-TU and bind the survivors. No -l filtering here.
            var objImports = ImportFieldSpecs(
                irBuilder.ProtoOnlyReferenced.Values
                    .Where(s => s.Type is not Ir.CType.Func { Variadic: true }).ToList());
            var objDefs = irBuilder.Functions.Select(f => f.Sym.Name)
                .Concat(irBuilder.Globals.Select(g => g.Sym.Name))
                .Distinct(StringComparer.Ordinal);
            return SerializeFragment(cg.Functions, new Dictionary<string, string>(), cg.Aliases, cg.Globals, cg.MainArity,
                objImports, objDefs);
        }
        return BuildShell(cg.MainArity, cg.Functions, cg.Structs, cg.Aliases, cg.Globals, fileBased, libraryMode, cg.Exports, debugHeap, importsClass, importsAreStatic);
    }

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

    /// <summary>
    /// Compile <paramref name="inputPaths"/> to a WebAssembly-text (<c>.wat</c>)
    /// module — the second output target. Shares the whole front-end with
    /// <see cref="EmitCSharp"/> through <see cref="BuildIr"/>; only the backend
    /// projection differs (<see cref="Ir.WatBackend"/> instead of
    /// <see cref="Backends.CSharpBackend"/> + <see cref="BuildShell"/>). Milestone 1 emits the
    /// freestanding integer slice; constructs outside it raise
    /// <see cref="CompileException"/> (an <see cref="Ir.IrUnsupportedException"/>).
    /// </summary>
    public static string EmitWat(
        IReadOnlyList<string> inputPaths,
        IReadOnlyList<string>? includeDirs = null,
        IReadOnlyList<string>? defines = null,
        CDialect? dialect = null,
        bool pedantic = false,
        bool pedanticErrors = false)
    {
        var irBuilder = BuildIr(inputPaths, includeDirs, defines, dialect, pedantic, pedanticErrors, new Backends.WatNameLegalizer());
        return Backends.WatBackend.Run(irBuilder);
    }

    // ---- separate compilation (`--emit=obj` + link) -------------------------
    // dotcc normally whole-program-compiles all TUs in one pass. To slot into a
    // build system (CMake/make) that compiles each `.c` to an object then links,
    // we split: `EmitObject` emits one TU's C# fragment (the LTO-style
    // intermediate), `LinkObjects` merges fragments — deduping shared types —
    // and wraps them in the shell + runtime exactly as whole-program emit does.

    // Marker lines delimiting a `.cs` object fragment. Comment-prefixed so a
    // fragment is still (almost) valid C#, and so the markers can't collide with
    // real emitted code.
    private const string FragMain   = "//!!dotcc-obj main:";
    private const string FragType   = "//!!dotcc-obj type:";
    private const string FragSect   = "//!!dotcc-obj section:"; // aliases|globals|functions
    // Import mode in separate compilation: `-l` is known only at LINK time, so each
    // fragment serializes its import CANDIDATES (proto-only, called, non-system,
    // non-variadic — `import:<name> <cs-fn-ptr-type>`) and the names it DEFINES
    // (`def:<name>`, functions + globals). The link step keeps a candidate iff no
    // fragment defines it, then binds the survivors GOT-style (see LinkObjects).
    private const string FragImport = "//!!dotcc-obj import:"; // import:<name> <delegate* unmanaged[Cdecl]<…>>
    private const string FragDef    = "//!!dotcc-obj def:";    // def:<name> (this TU defines it)

    // The uniform "magic" first line every dotcc-generated `.cs` carries, so any
    // file can be classified at a glance:
    //   //!dotcc program <v>   — a complete program (file/csproj/build/-shared)
    //   //!dotcc object  <v>   — a per-TU object fragment (--emit=obj), for linking
    // (A file-based program's `#:property` directives precede it; otherwise it's
    // line 1.) Scan the first few lines for these.
    private const string MagicObject = "//!dotcc object";

    /// <summary>Emit a single translation unit as a `.cs` object fragment.</summary>
    public static string EmitObject(
        string inputPath,
        IReadOnlyList<string>? includeDirs = null,
        IReadOnlyList<string>? defines = null,
        CDialect? dialect = null,
        bool pedantic = false,
        bool pedanticErrors = false)
        => EmitCSharp(new[] { inputPath }, includeDirs, defines,
                      fileBased: false, libraryMode: false, dialect, pedantic, pedanticErrors, asObject: true);

    private static string SerializeFragment(
        string functions, IReadOnlyDictionary<string, string> typeDecls, string aliases, string globals, int mainArity,
        IReadOnlyList<(string Name, string FieldType)> importSpecs, IEnumerable<string> defNames)
    {
        var sb = new StringBuilder();
        sb.Append(MagicObject).Append(" 1 — link with `dotcc <objs> -o <out>`.\n");
        sb.Append(FragMain).Append(mainArity).Append('\n');
        // Import candidates + defined names, for the link step's resolution. Names
        // have no spaces (C identifiers), so the type — which does (`delegate*
        // unmanaged[Cdecl]<int, int>`) — is everything after the first space.
        foreach (var (name, ft) in importSpecs) { sb.Append(FragImport).Append(name).Append(' ').Append(ft).Append('\n'); }
        foreach (var d in defNames) { sb.Append(FragDef).Append(d).Append('\n'); }
        // Types are tagged by name so the link step can union them across TUs.
        foreach (var (name, text) in typeDecls)
        {
            sb.Append(FragType).Append(name).Append('\n').Append(text);
        }
        sb.Append(FragSect).Append("aliases\n").Append(aliases);
        sb.Append(FragSect).Append("globals\n").Append(globals);
        sb.Append(FragSect).Append("functions\n").Append(functions);
        return sb.ToString();
    }

    /// <summary>
    /// Link `.cs` object fragments (from <see cref="EmitObject"/>) into one
    /// program: concatenate functions, union types/aliases/globals (deduping a
    /// shared header's declarations), then wrap in the shell + runtime.
    /// </summary>
    public static string LinkObjects(
        IReadOnlyList<string> objectPaths, bool fileBased = true, bool libraryMode = false, bool debugHeap = false,
        ImportOptions? imports = null)
    {
        var typeByName = new Dictionary<string, string>(StringComparer.Ordinal); // first wins
        var typeOrder = new List<string>();
        var aliasLines = new List<string>();
        var aliasSeen = new HashSet<string>(StringComparer.Ordinal);
        var globalLines = new List<string>();
        var globalSeen = new HashSet<string>(StringComparer.Ordinal);
        var functions = new StringBuilder();
        var mainArity = -1;
        // Import resolution across fragments: a candidate name → its fn-ptr type, and
        // every name some fragment DEFINES. A candidate survives iff no fragment defines it.
        var importSpecs = new Dictionary<string, string>(StringComparer.Ordinal);
        var definedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in objectPaths)
        {
            var text = File.ReadAllText(path).ReplaceLineEndings("\n");
            if (!text.Contains(MagicObject, StringComparison.Ordinal))
            {
                throw new CompileException(
                    $"'{Path.GetFileName(path)}' is not a dotcc object — no '{MagicObject}' marker. " +
                    "Link expects `--emit=obj` fragments, not a program or hand-written .cs.");
            }
            // Walk the fragment line by line, routing into the current bucket.
            string section = "";            // "type:<name>" | "aliases" | "globals" | "functions"
            var buf = new StringBuilder();
            void FlushType()
            {
                if (section.StartsWith("type:", StringComparison.Ordinal))
                {
                    var name = section["type:".Length..];
                    if (!typeByName.ContainsKey(name)) { typeByName[name] = buf.ToString(); typeOrder.Add(name); }
                }
                buf.Clear();
            }
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith(FragMain, StringComparison.Ordinal))
                {
                    if (int.TryParse(line[FragMain.Length..], out var m) && m >= 0) { mainArity = m; }
                }
                else if (line.StartsWith(FragImport, StringComparison.Ordinal))
                {
                    // import:<name> <type> — name is space-free, type is the rest.
                    var rest = line[FragImport.Length..];
                    var sp = rest.IndexOf(' ');
                    if (sp > 0) { importSpecs[rest[..sp]] = rest[(sp + 1)..]; }
                }
                else if (line.StartsWith(FragDef, StringComparison.Ordinal))
                {
                    definedNames.Add(line[FragDef.Length..]);
                }
                else if (line.StartsWith(FragType, StringComparison.Ordinal))
                {
                    FlushType();
                    section = "type:" + line[FragType.Length..];
                }
                else if (line.StartsWith(FragSect, StringComparison.Ordinal))
                {
                    FlushType();
                    section = line[FragSect.Length..];
                }
                else if (section.StartsWith("type:", StringComparison.Ordinal))
                {
                    buf.Append(line).Append('\n');
                }
                else if (section == "aliases")
                {
                    if (line.Length > 0 && aliasSeen.Add(line)) { aliasLines.Add(line); }
                }
                else if (section == "globals")
                {
                    if (line.Length > 0 && globalSeen.Add(line)) { globalLines.Add(line); }
                }
                else if (section == "functions")
                {
                    functions.Append(line).Append('\n');
                }
            }
            FlushType();
        }

        if (!libraryMode && mainArity < 0)
        {
            throw new CompileException("no `main` function defined in any linked object.");
        }

        var structDecls = new StringBuilder();
        foreach (var name in typeOrder) { structDecls.Append(typeByName[name]); }
        var aliasText = aliasLines.Count > 0 ? string.Join("\n", aliasLines) + "\n" : "";
        var globalText = globalLines.Count > 0 ? string.Join("\n", globalLines) + "\n" : "";
        // Import mode at link: bind the candidates no fragment defines (a name defined
        // in any object — function or global — is resolved internally, not imported).
        // Without `-l`, survivors stay unresolved → the same CS0103 as a normal link.
        var importsClass = "";
        if (imports is { LinkLibraries.Count: > 0 })
        {
            var survivors = importSpecs
                .Where(kv => !definedNames.Contains(kv.Key))
                .Select(kv => (kv.Key, kv.Value))
                .OrderBy(p => p.Item1, StringComparer.Ordinal)
                .ToList();
            if (survivors.Count > 0) { importsClass = RenderImportsClass(survivors, imports, libraryMode); }
        }
        return BuildShell(mainArity, functions.ToString(), structDecls.ToString(), aliasText, globalText,
                          fileBased, libraryMode, System.Array.Empty<EmitHelpers.Export>(), debugHeap, importsClass);
    }

    /// <summary>
    /// Run the preprocessor over <paramref name="inputPaths"/> and write the
    /// post-expansion token stream (one line per token, prefixed with the
    /// input filename comment) to <paramref name="output"/>.
    /// </summary>
    public static void Preprocess(
        IReadOnlyList<string> inputPaths,
        TextWriter output,
        IReadOnlyList<string>? includeDirs = null,
        IReadOnlyList<string>? defines = null,
        CDialect? dialect = null)
    {
        var includeMap = BuildIncludeMap(inputPaths, includeDirs);
        var lexerTable = C.BuildLexer();
        var seededDefines = SeedDialectDefines(dialect ?? CDialect.Default, defines);
        foreach (var unitPath in inputPaths)
        {
            output.WriteLine($"# {unitPath}");
            var source = SpliceLineContinuations(File.ReadAllText(unitPath));
            var pre = new CPreprocessor(lexerTable, includeMap, seededDefines);
            pre.SetActiveFilename(Path.GetFileName(unitPath));
            using var lexer = BytesLexer.FromString(source, lexerTable);
            using var preproc = C.WrapPreprocessor(lexer, pre);
            preproc.ExpandFuncMacro = pre.ExpandFuncMacro;
            // -E mode also routes through MacroExpander so function-like
            // macro expansion is visible in the dumped token stream.
            using var macroExp = new MacroExpander(preproc, pre);
            try
            {
                while (macroExp.MoveNext())
                {
                    var t = macroExp.Current;
                    output.Write(t.Content is string s ? s : t.Content?.ToString());
                    output.Write(' ');
                }
            }
            catch (global::LALR.CC.LexicalGrammar.LexerException ex)
            {
                throw new CompileException($"lex failed in {Path.GetFileName(unitPath)}: {ex.Message}", ex);
            }
            output.WriteLine();
        }
    }

    /// <summary>
    /// Build a Make-format dependency rule for one translation unit — the
    /// <c>-MD</c> / <c>-MMD</c> depfile contents. The rule lists
    /// <paramref name="targets"/> as the rule target(s) and the source plus
    /// every <c>#include</c>d header (transitively) as prerequisites, so
    /// CMake / Ninja / make can track header→TU dependencies and recompile a
    /// unit when any header it pulls in changes.
    /// </summary>
    /// <param name="includeSystemHeaders">When false (<c>-MMD</c>), drop
    /// headers included via the angle <c>&lt;...&gt;</c> form. When true
    /// (<c>-MD</c>), keep them — though the synthetic system headers carry no
    /// disk path and so never appear regardless.</param>
    /// <remarks>
    /// Runs a focused preprocess-only pass (it drives the same lexer →
    /// preprocessor → macro-expander pipeline the compile uses, then discards
    /// the tokens) purely to collect the include set. Conditional compilation
    /// is honoured — a header behind a false <c>#if</c> is not listed. Only
    /// headers that resolve to a real on-disk file become prerequisites;
    /// embedded synthetic headers (no path) are skipped, since there is
    /// nothing for the build tool to stat.
    /// </remarks>
    public static string EmitDependencyRule(
        string sourcePath,
        IReadOnlyList<string> targets,
        bool includeSystemHeaders,
        IReadOnlyList<string>? includeDirs = null,
        IReadOnlyList<string>? defines = null,
        CDialect? dialect = null)
    {
        var (content, paths) = BuildIncludeMaps(new[] { sourcePath }, includeDirs);
        var lexerTable = C.BuildLexer();
        var seededDefines = SeedDialectDefines(dialect ?? CDialect.Default, defines);

        var source = SpliceLineContinuations(File.ReadAllText(sourcePath));
        var pre = new CPreprocessor(lexerTable, content, seededDefines, quiet: true);
        pre.SetActiveFilename(Path.GetFileName(sourcePath));
        var lexer = BytesLexer.FromString(source, lexerTable);
        var preproc = C.WrapPreprocessor(lexer, pre);
        preproc.ExpandFuncMacro = pre.ExpandFuncMacro;
        using (lexer)
        using (preproc)
        using (var macroExp = new MacroExpander(preproc, pre))
        {
            // Drain — every #include the preprocessor reaches (respecting
            // #if/#ifdef conditionals) fires OnInclude, which records the
            // header in pre.IncludedHeaders.
            try
            {
                while (macroExp.MoveNext()) { /* tokens discarded; we only want the include set */ }
            }
            catch (global::LALR.CC.LexicalGrammar.LexerException ex)
            {
                throw new CompileException($"lex failed in {Path.GetFileName(sourcePath)}: {ex.Message}", ex);
            }
        }

        var prereqs = new List<string> { sourcePath };
        foreach (var (name, isSystem) in pre.IncludedHeaders)
        {
            if (isSystem && !includeSystemHeaders) { continue; }   // -MMD: drop <...> headers
            if (paths.TryGetValue(name, out var path)) { prereqs.Add(path); }
            // else: a synthetic/embedded header with no disk path — nothing to stat.
        }
        return FormatDependencyRule(targets, prereqs);
    }

    /// <summary>
    /// Render a Make dependency rule: <c>target...: prereq...</c> with one
    /// prerequisite per line, joined by <c> \</c> line continuations.
    /// Paths are normalized to forward slashes (the canonical form make /
    /// ninja / CMake all accept on Windows, and the only safe one given
    /// backslash is Make's escape character) and make-special characters
    /// (space, <c>#</c>, <c>$</c>) are escaped.
    /// </summary>
    private static string FormatDependencyRule(IReadOnlyList<string> targets, IReadOnlyList<string> prereqs)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < targets.Count; i++)
        {
            if (i > 0) { sb.Append(' '); }
            sb.Append(EscapeMakePath(targets[i]));
        }
        sb.Append(':');
        foreach (var p in prereqs)
        {
            sb.Append(" \\\n  ").Append(EscapeMakePath(p));
        }
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Normalize separators to <c>/</c> and escape the characters Make treats
    /// specially in a prerequisite list (<c>space</c>, <c>#</c>, <c>$</c>).
    /// </summary>
    private static string EscapeMakePath(string path)
    {
        var sb = new StringBuilder(path.Length + 8);
        foreach (var c in path.Replace('\\', '/'))
        {
            switch (c)
            {
                case ' ':
                case '#':
                    sb.Append('\\').Append(c);
                    break;
                case '$':
                    sb.Append("$$");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Build the csproj scaffold paired with the non-file-based shell from
    /// <see cref="EmitCSharp"/>. The frontend exe writes both into the output
    /// dir for the default csproj/build modes. When <paramref name="libraryMode"/>
    /// is true, configures <c>NativeLib=Shared</c> + <c>PublishAot=true</c>
    /// so <c>dotnet publish</c> produces a C-callable native shared library
    /// (<c>.dll</c> / <c>.so</c> / <c>.dylib</c>).
    /// </summary>
    public static string BuildGeneratedCsproj(
        bool libraryMode = false, string assemblyName = "dotcc-out", IReadOnlyList<string>? staticArchives = null)
    {
        // Static archives (.a/.lib): link them into the NativeAOT image. <DirectPInvoke>
        // tells ILC to resolve the [DllImport] stubs statically; <NativeLibrary> hands
        // the archive paths to the native linker. Forces PublishAot (static linking is
        // a publish-only operation — there's no CoreCLR static-link step). Empty when
        // no archive inputs were given.
        var staticItems = "";
        if (staticArchives is { Count: > 0 })
        {
            var sb = new StringBuilder();
            sb.Append("  <PropertyGroup>\n    <PublishAot>true</PublishAot>\n  </PropertyGroup>\n");
            sb.Append("  <ItemGroup>\n");
            foreach (var name in staticArchives.Select(StaticArchiveDllName).Distinct(StringComparer.Ordinal))
            {
                sb.Append($"    <DirectPInvoke Include=\"{name}\" />\n");
            }
            foreach (var path in staticArchives)
            {
                sb.Append($"    <NativeLibrary Include=\"{path.Replace('\\', '/')}\" />\n");
            }
            sb.Append("  </ItemGroup>\n");
            staticItems = sb.ToString();
        }

        if (libraryMode)
        {
            return $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <!-- Generated by dotcc (library mode, -shared). -->
                  <PropertyGroup>
                    <OutputType>Library</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <RootNamespace>DotCCGenerated</RootNamespace>
                    <AssemblyName>{assemblyName}</AssemblyName>
                    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                    <Nullable>disable</Nullable>
                    <!-- NativeAOT shared-library knobs. Producing the actual
                         .so/.dll/.dylib requires `dotnet publish`. -->
                    <PublishAot>true</PublishAot>
                    <NativeLib>Shared</NativeLib>
                    <IsTrimmable>true</IsTrimmable>
                  </PropertyGroup>
                {staticItems}</Project>
                """;
        }
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <!-- Generated by dotcc. -->
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <RootNamespace>DotCCGenerated</RootNamespace>
                <AssemblyName>{assemblyName}</AssemblyName>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <Nullable>disable</Nullable>
              </PropertyGroup>
            {staticItems}</Project>
            """;
    }

    /// <summary>
    /// Build the predefined-macro list for the given dialect, prepended to
    /// any user <c>-D</c> defines. Names follow the C standard: always
    /// <c>__STDC__=1</c> and <c>__STDC_HOSTED__=1</c>; <c>__STDC_VERSION__</c>
    /// is set only for C95+ modes (C89/C90 leaves it undefined per the
    /// standard). User defines win on collision — they come later in the
    /// list, and <see cref="CPreprocessor"/>'s loop overwrites prior entries
    /// in the macro table.
    /// </summary>
    internal static string[] SeedDialectDefines(CDialect dialect, IReadOnlyList<string>? userDefines)
    {
        var seeded = new List<string>(4 + (userDefines?.Count ?? 0))
        {
            "__STDC__=1",
            "__STDC_HOSTED__=1",
            // Compiler identification — analogous to clang's `__clang__`
            // and gcc's `__GNUC__`. User code can `#ifdef __dotcc__` to
            // branch on the compiler identity in mixed-source projects.
            "__dotcc__=1",
            // Data-model identification. dotcc IS an LP64 compiler by
            // construction — `long` lowers to C# `long` (64-bit
            // unconditionally, the documented widening of MSVC-Windows's
            // 32-bit `long`) and pointers are 8 bytes (the <stdint.h> /
            // offsetof layout model). Portable C decides its pointer-tagging
            // and integer-width strategy from exactly these macros
            // (chibi-scheme's SEXP_64_BIT probes __LP64__ et al.); without
            // them it would mis-configure for 32-bit and miscompute at
            // runtime, not just fail to compile.
            "__LP64__=1",
            "__SIZEOF_POINTER__=8",
            "__SIZEOF_LONG__=8",
            // dotcc's wchar_t is the MSVC shape — an unsigned 16-bit UTF-16 code
            // unit (→ C# char), NOT gcc/Linux's 32-bit wchar_t. Advertise the
            // width so portable code that branches on __SIZEOF_WCHAR_T__ (or the
            // __WCHAR_*__ range macros) configures for 2-byte wide chars.
            "__SIZEOF_WCHAR_T__=2",
            "__WCHAR_MIN__=0",
            "__WCHAR_MAX__=0xffff",
            // C23 #embed resource-status constants — the three values
            // `__has_embed(...)` evaluates to, so code can compare against the
            // named constant (`#if __has_embed("x") == __STDC_EMBED_FOUND__`).
            "__STDC_EMBED_NOT_FOUND__=0",
            "__STDC_EMBED_FOUND__=1",
            "__STDC_EMBED_EMPTY__=2",
        };
        var stdcVersion = dialect.StdcVersionLiteral;
        if (stdcVersion is not null)
        {
            seeded.Add($"__STDC_VERSION__={stdcVersion}");
        }
        if (userDefines is not null) { seeded.AddRange(userDefines); }
        return seeded.ToArray();
    }

    /// <summary>
    /// C translation phase 2: splice out backslash-newline line continuations
    /// (a <c>\</c> immediately followed by a newline) so the byte lexer never
    /// sees the stray <c>\</c>. Handles both <c>\</c>+LF and <c>\</c>+CRLF.
    /// Applied to every source string before it reaches the lexer — the
    /// translation unit AND every header (user + synthetic). .NET's
    /// <see cref="string.Replace(string,string)"/> is sequential and
    /// non-overlapping, so <c>\\</c> at end of line (an escaped backslash)
    /// correctly leaves a single <c>\</c>.
    /// </summary>
    /// <remarks>
    /// Caveat: this collapses physical lines, so <c>__LINE__</c> and error
    /// line numbers drift for lines following a continuation. Acceptable for
    /// now (diagnostics only); a faithful logical→physical line map is future
    /// work. The fast path returns the input untouched when it has no
    /// backslash at all (the common case), so non-macro-heavy code pays
    /// nothing.
    /// </remarks>
    internal static string SpliceLineContinuations(string source)
        => source.IndexOf('\\') < 0
            ? source
            : source.Replace("\\\r\n", string.Empty).Replace("\\\n", string.Empty);

    internal static IncludeMap BuildIncludeMap(
        IReadOnlyList<string> inputPaths,
        IReadOnlyList<string>? includeDirs)
        => BuildIncludeMaps(inputPaths, includeDirs).Content;

    /// <summary>
    /// Single-level enumeration options for the hand-rolled recursive walk below.
    /// <see cref="EnumerationOptions.RecurseSubdirectories"/> is deliberately FALSE —
    /// <see cref="EnumerateIncludablesResilient"/> recurses one directory at a time so
    /// a single failing subtree can be skipped instead of tearing down the whole walk.
    /// </summary>
    private static readonly EnumerationOptions OneLevelScan = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,        // skip chmod-700 / ACL'd dirs (UnauthorizedAccess)
        ReturnSpecialDirectories = false, // no "." / ".."
        AttributesToSkip = 0,
        BufferSize = 65536,
    };

    /// <summary>One directory entry, projected straight from the OS enumeration
    /// buffer — just the fields the header scan needs. <see cref="FileSystemEnumerable{T}"/>
    /// hands the transform a <c>ref FileSystemEntry</c>, so reading <c>Name</c> +
    /// attributes here allocates only the name string (vs <c>Directory.EnumerateFiles</c>,
    /// which materializes a full path string for EVERY entry whether wanted or not).</summary>
    private readonly record struct DirEntry(string Name, bool IsDirectory, bool IsReparsePoint);

    private static IEnumerable<DirEntry> EnumerateOneLevel(string dir) =>
        new System.IO.Enumeration.FileSystemEnumerable<DirEntry>(
            dir,
            static (ref System.IO.Enumeration.FileSystemEntry e) => new DirEntry(
                e.FileName.ToString(),
                (e.Attributes & FileAttributes.Directory) != 0,
                (e.Attributes & FileAttributes.ReparsePoint) != 0),
            OneLevelScan);

    /// <summary>
    /// Recursively enumerate includable files (<c>.h</c> / <c>.c</c>) under
    /// <paramref name="root"/>, yielding each match's full path and its <c>/</c>-separated
    /// path relative to <paramref name="root"/> (built incrementally as we descend, so no
    /// per-file <see cref="Path.GetRelativePath"/> re-parse).
    ///
    /// A hand-rolled walk — NOT <c>Directory.EnumerateFiles(..., AllDirectories)</c> —
    /// because dotcc auto-adds each input file's OWN directory as a search dir, and that
    /// is frequently a big shared dir (the system TEMP root, where the test suite drops
    /// scratch <c>.c</c> files among thousands of unrelated installer subtrees). The
    /// framework's recursive overload tears down the ENTIRE enumeration on the first
    /// directory that throws — <see cref="EnumerationOptions.IgnoreInaccessible"/> only
    /// swallows <see cref="UnauthorizedAccessException"/>, NOT a transient
    /// <see cref="IOException"/> ("insufficient system resources") that an enormous tree
    /// provokes. Walking one level at a time under a per-directory guard skips the bad
    /// subtree while the headers we actually need still resolve. Reparse points
    /// (junctions / symlinks) are skipped to avoid cycles. Technique mirrors
    /// <c>../filetreewalker</c>'s <c>Walker</c> (FileSystemEnumerable per level + manual
    /// recursion + per-level try/catch), trimmed to the header-scan need.
    /// </summary>
    private static IEnumerable<(string FullPath, string Rel)> EnumerateIncludablesResilient(string root)
    {
        // Explicit stack of (directory, its rel prefix) — "" at the root.
        var pending = new Stack<(string Dir, string RelPrefix)>();
        pending.Push((root, ""));
        while (pending.Count > 0)
        {
            var (dir, prefix) = pending.Pop();
            List<DirEntry> entries;
            try
            {
                // Drain this ONE level under the guard: FileSystemEnumerable can throw
                // mid-enumeration (MoveNext), so a bad directory is contained here.
                entries = new List<DirEntry>(EnumerateOneLevel(dir));
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var e in entries)
            {
                if (e.IsReparsePoint) { continue; } // don't follow junctions/symlinks
                var rel = prefix.Length == 0 ? e.Name : prefix + "/" + e.Name;
                if (e.IsDirectory)
                {
                    pending.Push((Path.Combine(dir, e.Name), rel));
                }
                else if (e.Name.EndsWith(".h", StringComparison.OrdinalIgnoreCase)
                      || e.Name.EndsWith(".c", StringComparison.OrdinalIgnoreCase))
                {
                    yield return (Path.Combine(dir, e.Name), rel);
                }
            }
        }
    }

    /// <summary>
    /// Resolve headers: scan every <c>-I</c> directory + every <c>.h</c>
    /// alongside each <c>.c</c> + the synthetic system headers. Returns both
    /// the <c>name → content</c> map the preprocessor reads AND a
    /// <c>name → on-disk path</c> map used to render dependency files
    /// (<c>-MD</c>/<c>-MMD</c>). Last-wins (in the same dir order) so a user
    /// <c>-I</c> overrides a system header, and the two maps stay consistent.
    /// The synthetic system headers are embedded resources with no disk path,
    /// so they appear only in <c>Content</c> — never in <c>Paths</c>; that is
    /// exactly what keeps them out of the dependency file (nothing for
    /// make/ninja to stat).
    /// </summary>
    private static (IncludeMap Content, Dictionary<string, string> Paths) BuildIncludeMaps(
        IReadOnlyList<string> inputPaths,
        IReadOnlyList<string>? includeDirs)
    {
        var eager = new Dictionary<string, string>(SystemHeaders, StringComparer.Ordinal);
        var lazy = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var dirs = (includeDirs ?? Array.Empty<string>())
            .Concat(inputPaths.Select(p => Path.GetDirectoryName(Path.GetFullPath(p)) ?? "."))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) { continue; }
            // One resilient walk collects BOTH headers and includable `.c` files.
            // A file directly in the dir registers under its bare name
            // (`#include "lstate.h"`). One in a SUBDIRECTORY registers under its
            // dir-relative path with `/` separators, so the subdirectory-qualified
            // include form resolves too: `-I include` + `#include "chibi/sexp.h"` →
            // key `chibi/sexp.h` (chibi-scheme's layout; ditto <sys/types.h> trees).
            // Depth-0 names keep the exact pre-recursion semantics — a nested header
            // is deliberately NOT registered bare, matching gcc.
            foreach (var (full, rel) in EnumerateIncludablesResilient(dir))
            {
                if (rel.EndsWith(".h", StringComparison.OrdinalIgnoreCase))
                {
                    // Header content is read eagerly. A header that vanishes/locks
                    // between scan and read is skipped, not fatal — the same
                    // race-tolerance the lazy `.c` path already has below.
                    string content;
                    try { content = SpliceLineContinuations(File.ReadAllText(full)); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    eager[rel] = content;
                    paths[rel] = full;
                }
                else
                {
                    // `.c` registers too: `#include "opt/fcall.c"` (chibi vm.c) is the
                    // single-translation-unit composition idiom — a quoted include is
                    // a textual splice regardless of extension. Registered by PATH and
                    // read only on actual inclusion: an input's directory can hold many
                    // unrelated `.c` files (a temp dir, a whole source tree), and
                    // reading them all eagerly is wasted I/O — and a race against other
                    // processes' transient files.
                    lazy[rel] = full;
                    paths[rel] = full;
                }
            }
        }
        return (new IncludeMap(eager, lazy), paths);
    }

    /// <summary>
    /// The preprocessor's <c>#include</c> resolution map. Header (<c>.h</c>)
    /// content is loaded eagerly at map-build time (the historical behavior);
    /// includable <c>.c</c> files are registered by path and read on first
    /// actual inclusion, with the content cached. An unreadable lazy entry
    /// (deleted/locked between scan and use) reports as unresolvable rather
    /// than failing the compile.
    /// </summary>
    internal sealed class IncludeMap
    {
        private readonly Dictionary<string, string> _eager;
        private readonly Dictionary<string, string> _lazyPaths;

        internal IncludeMap(Dictionary<string, string> eager, Dictionary<string, string> lazyPaths)
        {
            _eager = eager;
            _lazyPaths = lazyPaths;
        }

        /// <summary>All-eager map (no lazy entries) — test convenience.</summary>
        internal IncludeMap(Dictionary<string, string> eager)
            : this(eager, new Dictionary<string, string>(StringComparer.Ordinal)) { }

        public bool ContainsKey(string name) => _eager.ContainsKey(name) || _lazyPaths.ContainsKey(name);

        public bool TryGetValue(string name, out string content)
        {
            if (_eager.TryGetValue(name, out content!)) { return true; }
            if (_lazyPaths.TryGetValue(name, out var path))
            {
                try
                {
                    content = SpliceLineContinuations(File.ReadAllText(path));
                }
                catch (IOException) { content = ""; return false; }
                catch (UnauthorizedAccessException) { content = ""; return false; }
                _eager[name] = content; // cache for the next TU in this compile
                return true;
            }
            content = "";
            return false;
        }
    }

    internal static string BuildShell(
        int mainArity,
        string emittedFnList,
        string structDecls,
        string usingAliases,
        string globals,
        bool fileBased,
        bool libraryMode,
        IReadOnlyList<EmitHelpers.Export> exports,
        bool debugHeap = false,
        string importsClass = "",
        bool importsAreStatic = false)
    {
        if (libraryMode)
        {
            return BuildLibraryShell(emittedFnList, structDecls, usingAliases, globals, exports, importsClass, importsAreStatic);
        }
        // Import mode: surface the import table by bare name and splice it into the
        // type-decls section. A GOT (-l) table is bound before main; static [DllImport]
        // stubs bind at the native link, so no startup call. All collapse to nothing
        // when no import was requested, so a normal compile is unchanged.
        var importsClassName = importsAreStatic ? "DotCcStaticImports" : "DotCcImports";
        var importsUsing = importsClass.Length > 0 ? $"\nusing static {importsClassName};" : "";
        var importsBind = (importsClass.Length > 0 && !importsAreStatic) ? "DotCcImports.__BindAll();\n" : "";
        // Embedded DotCC.Libc runtime block — spliced into the heredoc
        // below so the emitted .cs is self-contained even without a
        // <PackageReference Include="DotCC.Libc"> in scope.
        var runtimeBlock = _runtimeBlock.Value;
        var header = fileBased ? "#:property AllowUnsafeBlocks=true\n\n" : string.Empty;
        // -fsanitize=address: flip the checked debug heap on before any user
        // code (or the embedded runtime) allocates, so every malloc/free
        // downstream carries the header + redzone the checked `free` validates.
        // (DOTCC_DEBUG_HEAP=1 is the equivalent runtime override; see Libc.)
        var debugHeapInit = debugHeap
            ? "Libc.EnableDebugHeap(); // -fsanitize=address: checked malloc/free\n            "
            : string.Empty;
        // User functions live as STATIC METHODS of `DotCcProgram` (not top-level
        // local functions). A top-level local function can't be addressed (`&fn`),
        // stored in a function-pointer table (Lua's `luaL_Reg`), or referenced from
        // several C# contexts (CS8801/CS8422/CS8787) — class methods can. The
        // visitor's `static unsafe` becomes `internal static unsafe` so `using
        // static DotCcProgram;` surfaces them by bare name everywhere (the entry's
        // `main(...)` call, file-scope `&fn` initializers, and inter-function calls).
        var indentedFns = IndentBlock(
            emittedFnList.Replace("static unsafe ", "internal static unsafe "), "    ");
        var entry = mainArity switch
        {
            0 => "return main();",
            1 => "return main(args.Length);",
            2 =>
                """
                unsafe
                {
                    // Real C: argv[0] = program path, argv[1..] = user args, argc = total.
                    // .NET hands us only the user args, so synthesize argv[0] from the
                    // running assembly to match clang's vector layout.
                    int argc = args.Length + 1;
                    // +1 for the C-standard NULL sentinel at argv[argc]
                    byte** argv = (byte**)NativeMemory.Alloc((nuint)(argc + 1) * (nuint)sizeof(byte*));
                    static byte* EncodeUtf8Nul(string s)
                    {
                        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
                        var slot = (byte*)NativeMemory.Alloc((nuint)(bytes.Length + 1));
                        for (int k = 0; k < bytes.Length; k++) { slot[k] = bytes[k]; }
                        slot[bytes.Length] = 0;
                        return slot;
                    }
                    argv[0] = EncodeUtf8Nul(
                        System.Environment.ProcessPath ?? System.AppContext.BaseDirectory);
                    for (int i = 0; i < args.Length; i++)
                    {
                        argv[i + 1] = EncodeUtf8Nul(args[i]);
                    }
                    argv[argc] = null; // C standard: argv[argc] == NULL
                    // argv is deliberately NOT freed. In C the argument vector is
                    // owned by the environment and lives for the whole process; a
                    // program may legally mutate it — overwrite an entry with a
                    // pointer it did NOT malloc (chibi's `-q` does `argv[i] =
                    // "-xchibi"`, a string literal) or reassign argv itself — so the
                    // original buffers can't be safely freed by slot here. Reclaiming
                    // it as the process exits would be pointless anyway (the OS does
                    // it); freeing a non-heap pointer aborts the run.
                    return main(argc, argv);
                }
                """,
            _ => throw new InvalidOperationException(
                $"dotcc: `main` must have 0, 1, or 2 parameters; got {mainArity}."),
        };

        return $$"""
            {{header}}//!dotcc program 1 — generated by dotcc (transpiled C → C#)
            // <auto-generated>
            // Emitted by dotcc from c.lalr.yaml + the input translation units.
            // </auto-generated>
            using System;
            using System.Globalization;
            using System.Numerics;
            using System.IO;
            using System.Runtime.InteropServices;
            using System.Runtime.CompilerServices;
            using System.Threading;
            using System.Collections.Generic;
            using System.Collections.Concurrent;
            using System.Text;
            using System.Net;
            using System.Net.Sockets;
            // ---- DotCC.Libc runtime (embedded source) ----------------
            // The Libc class (declared at file end, spliced in from the
            // ../DotCC.Libc/*.cs sources) holds every libc function +
            // every <math.h>/<tgmath.h> entry — each one with both
            // float and double overloads. Importing it statically here
            // means `sin(x)` / `printf("…")` / `malloc(n)` in user code
            // resolve by bare name to the matching method, and overload
            // resolution picks the right form. That's exactly what
            // <tgmath.h>'s _Generic macros do in real C; we get the
            // same dispatch for free without preprocessor machinery.
            using static Libc;
            // ---- File-scope variables + user functions ----------------
            // C globals are static fields of `DotCcGlobals`; user functions are
            // static methods of `DotCcProgram` (both declared at file end).
            // `using static` on both makes every global + function visible by bare
            // name everywhere — functions call each other and read globals
            // unqualified, the entry below calls `main(...)`, AND a file-scope
            // initializer can take a function's address (`&fn`, the `luaL_Reg`-table
            // idiom) by bare name across the class boundary (using-static surfaces
            // the method group).
            using static DotCcGlobals;
            using static DotCcProgram;{{importsUsing}}

            // ---- typedef'd `using` aliases (C# 12+ permits `using unsafe X = Y;`
            //      at file scope, ahead of top-level statements). Empty when no
            //      `typedef` declarations were seen.
            {{usingAliases}}
            // dotcc: run the program entry on a thread with a large stack. A native
            // C program runs on a multi-megabyte stack (Linux defaults to ~8 MB), but
            // .NET's default is ~1 MB — too shallow for deeply-recursive C. Lua's VM,
            // for instance, guards recursion with LUAI_MAXCCALLS (200 nested C calls)
            // on the assumption that 200 frames fit comfortably; on a 1 MB stack the
            // emitted frames overflow at ~100 and crash the runtime before Lua can
            // raise its own catchable "C stack overflow". Reserving 64 MB (virtual
            // address space, not committed memory) restores the native headroom so
            // such programs reach their own recursion guards and fault gracefully.
            {{debugHeapInit}}int __dotccExit = 0;
            var __dotccThread = new System.Threading.Thread(
                () => { __dotccExit = __DotCcEntry(); }, 64 * 1024 * 1024);
            __dotccThread.Start();
            __dotccThread.Join();
            return __dotccExit;

            int __DotCcEntry()
            {
            {{importsBind}}{{entry}}
            }


            // ---- user functions (static methods of DotCcProgram; see the
            //      `using static` note above — class methods, not top-level locals,
            //      so `&fn` / function-pointer tables / cross-context refs work) ----

            static unsafe class DotCcProgram
            {
            {{indentedFns}}
            }

            {{importsClass}}
            // ---- type declarations (must come last; C# requires top-level
            //      statements to precede type declarations) ----

            {{structDecls}}

            // C file-scope variables, collected as static fields. Empty
            // class when no globals are declared — harmless but kept for
            // shell-shape stability.
            static unsafe class DotCcGlobals
            {
            {{globals}}}

            // C-truthy → C# bool. The visitor wraps every conditional context
            // (`if`/`while`/`for`-cond) with `Cond.B(...)` so int- and
            // pointer-valued conditions (`while (1)`, `if (p)`, `while (--n)`)
            // typecheck. Overloads live on a static class because top-level
            // local functions in file-scoped programs can't be overloaded
            // (CS0128). Overload resolution picks the right form at compile
            // time: bool stays bool; int/double compare against zero; any
            // pointer implicitly converts to void* and compares against null.
            static class Cond
            {
                public static bool B(bool b) => b;
                public static bool B(int x) => x != 0;
                public static bool B(double x) => x != 0;
                public static unsafe bool B(void* p) => p != null;
                // A C scalar of ANY integer/float type is truthy when non-zero;
                // emit one exact overload per lowered numeric type. Without these,
                // a `byte`/`uint`/`long`/`ulong`/… argument (e.g. Lua's `lu_byte`
                // `allowhook`) is convertible to BOTH a built-in numeric overload
                // AND `CBool` (via a one-step user-defined conversion), so the call
                // is ambiguous (CS0121). An exact match beats the user-defined
                // CBool path, so each type resolves unambiguously.
                public static bool B(uint x) => x != 0;
                public static bool B(long x) => x != 0;
                public static bool B(ulong x) => x != 0;
                public static bool B(nint x) => x != 0;
                public static bool B(nuint x) => x != 0;
                public static bool B(sbyte x) => x != 0;
                public static bool B(byte x) => x != 0;
                public static bool B(short x) => x != 0;
                public static bool B(ushort x) => x != 0;
                public static bool B(float x) => x != 0;
                // Relational / logical results lower to CBool (C's int 0/1);
                // unwrap to bool for the conditional position they sit in.
                public static bool B(CBool b) => (int)b != 0;
            }

            {{runtimeBlock}}
            """;
    }

    /// <summary>
    /// Library-mode shell: wraps user functions inside <c>internal static class
    /// DotCcLib</c>, emits a <c>public static class DotCcExports</c> with
    /// <c>[UnmanagedCallersOnly(EntryPoint = "name")]</c> wrappers for every
    /// non-static C function (external linkage). NativeAOT publish turns the
    /// resulting assembly into a real C-callable <c>.dll</c>/<c>.so</c>/<c>.dylib</c>.
    /// </summary>
    private static string BuildLibraryShell(
        string emittedFnList,
        string structDecls,
        string usingAliases,
        string globals,
        IReadOnlyList<EmitHelpers.Export> exports,
        string importsClass = "",
        bool importsAreStatic = false)
    {
        // Import mode in a -shared lib: surface the table by bare name and splice it. A
        // GOT table binds in a static constructor (no entry point here); static [DllImport]
        // stubs bind at the lib's own publish. Empty when no import was requested.
        var importsUsing = importsClass.Length > 0
            ? $"\nusing static {(importsAreStatic ? "DotCcStaticImports" : "DotCcImports")};" : "";
        // Same embedded DotCC.Libc runtime block as exe mode — the
        // library and exe shells share the same set of stdlib functions;
        // only the framing (DotCcLib + DotCcExports vs. top-level
        // statements + `main`) differs.
        var runtimeBlock = _runtimeBlock.Value;
        // Visitor emits user fns as `static unsafe T name(...)` — class-member
        // default is private, which would block DotCcExports from calling
        // them. Promote to `public static unsafe …`. DotCcLib itself is
        // `internal`, so `public` here is effectively assembly-private:
        // external C consumers still only see DotCcExports' attributed methods.
        var publicFns = emittedFnList.Replace("static unsafe ", "public static unsafe ");
        // Indent the user-function block so it lives correctly inside the class body.
        var indentedFns = IndentBlock(publicFns, "    ");

        // Build the [UnmanagedCallersOnly] wrappers. Skip varargs functions —
        // C# `params` arrays aren't a valid signature for the attribute and
        // can't survive AOT publish.
        var exportsBlock = new StringBuilder();
        foreach (var e in exports)
        {
            if (e.Params.Contains("params VaArg[]", StringComparison.Ordinal))
            {
                exportsBlock.Append($"    // dotcc: '{e.Name}' has varargs — not exported (no UnmanagedCallersOnly support).\n");
                continue;
            }
            var argNames = ExtractArgNames(e.Params);
            // EntryPoint keeps the raw C name (the exported C-ABI symbol); the
            // C# wrapper method + the DotCcLib call escape any C#-keyword name.
            var csName = EmitHelpers.Id(e.Name);
            exportsBlock.Append($"    [UnmanagedCallersOnly(EntryPoint = \"{e.Name}\", CallConvs = new[] {{ typeof(CallConvCdecl) }})]\n");
            exportsBlock.Append($"    public static unsafe {e.ReturnType} {csName}({e.Params}) => DotCcLib.{csName}({argNames});\n\n");
        }

        return $$"""
            //!dotcc program 1 — generated by dotcc (transpiled C → C#, -shared)
            // <auto-generated>
            // Emitted by dotcc from c.lalr.yaml + the input translation units.
            // Library mode (-shared) — NativeAOT-publishable shared lib with
            // C-ABI exports via [UnmanagedCallersOnly].
            // </auto-generated>
            using System;
            using System.Globalization;
            using System.Numerics;
            using System.IO;
            using System.Runtime.InteropServices;
            using System.Runtime.CompilerServices;
            using System.Threading;
            using System.Collections.Generic;
            using System.Collections.Concurrent;
            using System.Text;
            using System.Net;
            using System.Net.Sockets;
            using static Libc;
            using static DotCcGlobals;{{importsUsing}}

            // ---- typedef'd `using` aliases (same as exe mode).
            {{usingAliases}}
            // User code lives in an internal class so calls between user fns
            // resolve directly (vs. [UnmanagedCallersOnly] methods which can
            // only be invoked via function pointer from C#). External C
            // consumers reach these through the DotCcExports wrappers below.
            internal static class DotCcLib
            {
            {{indentedFns}}
            }

            // C-ABI exports. Each wrapper trampoline delegates to the matching
            // DotCcLib method; NativeAOT inlines the trampoline at publish.
            public static class DotCcExports
            {
            {{exportsBlock}}}

            {{importsClass}}
            // ---- Type declarations (top-level — same as exe mode). ----
            {{structDecls}}

            // C file-scope variables, collected as static fields (same as
            // exe mode). DotCcLib reaches them via `using static DotCcGlobals;`
            // — adding that import here too so library-mode emits work.
            static unsafe class DotCcGlobals
            {
            {{globals}}}

            // C-truthy → C# bool. Same set of overloads as exe mode.
            static class Cond
            {
                public static bool B(bool b) => b;
                public static bool B(int x) => x != 0;
                public static bool B(double x) => x != 0;
                public static unsafe bool B(void* p) => p != null;
                // A C scalar of ANY integer/float type is truthy when non-zero;
                // emit one exact overload per lowered numeric type. Without these,
                // a `byte`/`uint`/`long`/`ulong`/… argument (e.g. Lua's `lu_byte`
                // `allowhook`) is convertible to BOTH a built-in numeric overload
                // AND `CBool` (via a one-step user-defined conversion), so the call
                // is ambiguous (CS0121). An exact match beats the user-defined
                // CBool path, so each type resolves unambiguously.
                public static bool B(uint x) => x != 0;
                public static bool B(long x) => x != 0;
                public static bool B(ulong x) => x != 0;
                public static bool B(nint x) => x != 0;
                public static bool B(nuint x) => x != 0;
                public static bool B(sbyte x) => x != 0;
                public static bool B(byte x) => x != 0;
                public static bool B(short x) => x != 0;
                public static bool B(ushort x) => x != 0;
                public static bool B(float x) => x != 0;
                // Relational / logical results lower to CBool (C's int 0/1);
                // unwrap to bool for the conditional position they sit in.
                public static bool B(CBool b) => (int)b != 0;
            }

            {{runtimeBlock}}
            """;
    }

    /// <summary>
    /// Indent every non-empty line of <paramref name="block"/> by
    /// <paramref name="prefix"/>. Used to drop the user-function string
    /// (emitted at top-level shape) into a class body.
    /// </summary>
    private static string IndentBlock(string block, string prefix)
    {
        if (string.IsNullOrEmpty(block)) { return block; }
        var sb = new StringBuilder(block.Length + 64);
        var first = true;
        foreach (var line in block.Split('\n'))
        {
            if (!first) { sb.Append('\n'); }
            first = false;
            if (line.Length == 0) { continue; }
            sb.Append(prefix).Append(line);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Extract bare argument names from a C# parameter list like
    /// <c>"int* arr, int n, Comparator cmp"</c> → <c>"arr, n, cmp"</c>.
    /// Used to generate <c>[UnmanagedCallersOnly]</c> wrapper call sites
    /// that delegate to the underlying impl with matching argument order.
    /// </summary>
    private static string ExtractArgNames(string paramList)
    {
        if (string.IsNullOrEmpty(paramList)) { return string.Empty; }
        var parts = paramList.Split(", ");
        var sb = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            var sp = p.LastIndexOf(' ');
            if (i > 0) { sb.Append(", "); }
            sb.Append(sp < 0 ? p : p[(sp + 1)..]);
        }
        return sb.ToString();
    }
}

/// <summary>
/// The stable public compile-error surface of <see cref="Compiler.EmitCSharp"/>:
/// thrown on parse/lex failure, an invalid type-specifier combination, a
/// dialect-gate violation under <c>-pedantic-errors</c>, an unsupported construct
/// (<see cref="DotCC.Ir.IrUnsupportedException"/>, a subclass), or when no
/// <c>main</c> is defined. The frontend exe catches it and maps to a non-zero exit
/// code with a clang-shaped diagnostic; tests assert on the message. Not sealed so
/// the IR layer can specialize it while callers still catch the one base type.
/// </summary>
public class CompileException : Exception
{
    public CompileException(string message) : base(message) { }
    public CompileException(string message, Exception inner) : base(message, inner) { }
}
