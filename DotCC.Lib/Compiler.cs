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
///     units to a single C# source string. The <see cref="EmitMode"/> selects the
///     output shape (file-based program, csproj-paired shell, shared library, or
///     object fragment).</item>
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
public static partial class Compiler
{
    /// <summary>
    /// Dispatch the inputs to the right <see cref="Frontends.IFrontend"/> and return the
    /// neutral <see cref="Ir.IrBuilder"/> the backends consume (<see cref="EmitCSharp"/>
    /// / the wat path). A <c>.zig</c>-only set routes to the Zig front-end; an all-C set
    /// to the C front-end; a MIXED <c>.c</c> + <c>.zig</c> set lowers both into one
    /// shared module (see <see cref="BuildMixedIr"/>). Kept as a private shim so the
    /// existing call sites stay unchanged.
    /// </summary>
    private static Ir.IrBuilder BuildIr(
        IReadOnlyList<string> inputPaths,
        IReadOnlyList<string>? includeDirs,
        IReadOnlyList<string>? defines,
        CDialect? dialect,
        Ir.INameLegalizer? names = null,
        WarningFlags warnings = WarningFlags.Default,
        bool testMode = false)
    {
        var request = new Frontends.FrontendRequest(
            inputPaths, includeDirs, defines, dialect, names, warnings, testMode);
        var anyZig = inputPaths.Any(IsZigSource);
        var anyC = inputPaths.Any(p => !IsZigSource(p));
        if (anyZig && anyC) { return BuildMixedIr(request); }
        Frontends.IFrontend frontend = anyZig ? new Frontends.ZigFrontend() : new Frontends.CFrontend();
        return frontend.BuildIr(request);
    }

    private static bool IsZigSource(string path) => path.EndsWith(".zig", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Build ONE IR module from a mixed <c>.c</c> + <c>.zig</c> translation-unit set.
    /// The C group is built first via <see cref="Frontends.CFrontend"/> (so its
    /// preprocessor/dialect-gate machinery, struct/enum/global emission, and C
    /// diagnostics all run normally); the Zig group then lowers INTO that same
    /// <see cref="Ir.IrBuilder"/> through <see cref="Frontends.ZigFrontend.AddUnits"/>,
    /// sharing the one name legalizer. The whole program therefore emits once down the
    /// existing backend path — structs preserved — and a call across the language
    /// boundary resolves at the C# level (every function is a <c>DotCcProgram</c>
    /// method called by bare name), exactly as separate compilation already does. Only
    /// the Zig group's diagnostics are flushed here (the C front-end already flushed
    /// its own), so a C warning isn't printed twice.
    /// </summary>
    private static Ir.IrBuilder BuildMixedIr(Frontends.FrontendRequest request)
    {
        var sharedNames = request.Names ?? new Backends.CSharpNameLegalizer();
        var cPaths = request.InputPaths.Where(p => !IsZigSource(p)).ToList();
        var zigPaths = request.InputPaths.Where(IsZigSource).ToList();

        var ir = new Frontends.CFrontend().BuildIr(request with { InputPaths = cPaths, Names = sharedNames });
        var flushed = ir.Diagnostics.Count;     // C diagnostics already flushed by CFrontend
        Frontends.ZigFrontend.AddUnits(ir, zigPaths, sharedNames);

        // Flush ONLY the diagnostics the Zig lowering added (skip the already-flushed C ones).
        var zigDiags = ir.Diagnostics.Skip(flushed).ToList();
        var zigErrors = zigDiags.Where(d => d.Severity == Ir.Severity.Error).ToList();
        if (zigErrors.Count > 0)
        {
            throw new CompileException(string.Join("\n", zigErrors.Select(d => "error: " + d)));
        }
        foreach (var w in zigDiags.Where(d => d.Severity == Ir.Severity.Warning))
        {
            Console.Error.WriteLine("dotcc: warning: " + w);
        }
        return ir;
    }

    /// <summary>
    /// Compile <paramref name="inputPaths"/> to a single C# source string.
    /// </summary>
    /// <param name="emit">The output shape (see <see cref="EmitMode"/>): a file-based
    /// program (default), a csproj-paired shell, a <c>-shared</c> shared library, or an
    /// <c>--emit=obj</c> object fragment. In <see cref="EmitMode.SharedLib"/> the emit is
    /// a NativeAOT-publishable shared library — user functions in
    /// <c>internal static class DotCcLib</c>, non-static C functions re-exported via
    /// <c>[UnmanagedCallersOnly]</c> in <c>public static class DotCcExports</c>, and no
    /// <c>main</c> required; every other shape emits the standalone-executable shell with
    /// a <c>return main(…);</c> entry.</param>
    public static string EmitCSharp(
        IReadOnlyList<string> inputPaths,
        IReadOnlyList<string>? includeDirs = null,
        IReadOnlyList<string>? defines = null,
        EmitMode emit = EmitMode.File,
        CDialect? dialect = null,
        bool debugHeap = false,
        ImportOptions? imports = null,
        WarningFlags warnings = WarningFlags.Default,
        bool testMode = false)
    {
        var libraryMode = emit == EmitMode.SharedLib;
        var asObject = emit == EmitMode.Object;
        var irBuilder = BuildIr(inputPaths, includeDirs, defines, dialect, warnings: warnings, testMode: testMode);
        // -Wconversion: collect narrowing-conversion warnings during codegen, then
        // flush to stderr. Off by default (the bit is clear unless -Wconversion set).
        var convGate = (warnings & WarningFlags.Conversion) != 0 ? new ConversionGate() : null;
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
        // exports); object mode links later; TEST mode runs the test manifest, not
        // `main` (a test file legitimately has none). Exe mode requires an entry point.
        if (!asObject && !libraryMode && !testMode && cg.MainArity < 0)
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
                objImports, objDefs, cg.MainReturnsVoid, cg.MainReturnsErrUnion, cg.MainErrPayloadIsVoid);
        }
        return BuildShell(cg.MainArity, cg.Functions, cg.Structs, cg.Aliases, cg.Globals, emit, cg.Exports, debugHeap, importsClass, importsAreStatic, cg.MainReturnsVoid, cg.MainReturnsErrUnion, cg.MainErrPayloadIsVoid, testMode, cg.Tests);
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
        WarningFlags warnings = WarningFlags.Default)
    {
        var irBuilder = BuildIr(inputPaths, includeDirs, defines, dialect, new Backends.WatNameLegalizer(), warnings);
        return Backends.WatBackend.Run(irBuilder);
    }

    /// <summary>
    /// Inventory a binary <c>.wasm</c> module and render a human-readable summary —
    /// section layout, entity counts, import/export surfaces, the post-MVP features
    /// the encoding uses, and a ranked opcode histogram. This is the public face of
    /// the WF0 surface reader (<see cref="Wasm.WasmModuleProbe"/>); it is READ-ONLY —
    /// dotcc cannot yet lift wasm to IR (WF1/WF2), so this reports structure, it does
    /// not compile. Fail-soft like the probe: a malformed module yields a summary that
    /// says so rather than throwing. Used by the web sandbox to show the shape of the
    /// wasm dotcc itself assembles (fable-web.md WEB6 / fable-wasm.md).
    /// </summary>
    public static string ProbeWasm(byte[] module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var report = Wasm.WasmModuleProbe.Probe(module);
        return FormatProbeReport(report, module.Length);
    }

    /// <summary>Render a single <see cref="Wasm.WasmProbeReport"/> as the compact,
    /// per-module summary the sandbox displays — the same field shape as the WF0
    /// corpus report's per-module block (<c>WasmSurfaceProbeTests.AppendModule</c>),
    /// plus the full ranked opcode histogram (a single sandbox module is small).</summary>
    private static string FormatProbeReport(Wasm.WasmProbeReport r, int byteLength)
    {
        var sb = new StringBuilder();
        sb.Append($"dotcc wasm probe — {byteLength:N0} bytes (read-only inventory; dotcc cannot yet lift wasm)\n");
        sb.Append($"status   : {(r.Status == Wasm.WasmProbeStatus.Ok ? "ok" : "MALFORMED")}\n");
        sb.Append($"sections : {(r.Sections.Count == 0 ? "(none)" : string.Join(", ", r.Sections.Select(s => $"{s.Name}({s.Size})")))}\n");
        sb.Append(
            $"counts   : {r.FunctionCount} funcs, {r.TypeCount} types, {r.TableCount} tables, " +
            $"{r.MemoryCount} memories, {r.GlobalCount} globals, {r.ElemCount} elems, {r.DataCount} datas" +
            $"{(r.HasStart ? ", start fn" : "")}{(r.HasNameSection ? ", name section" : "")}\n");
        sb.Append($"imports  : {(r.Imports.Count == 0 ? "(none)" : string.Join(", ", r.Imports.Select(i => $"{i.Module}.{i.Name} ({i.Kind})")))}\n");
        sb.Append($"exports  : {(r.Exports.Count == 0 ? "(none)" : string.Join(", ", r.Exports.Select(e => $"{e.Name} ({e.Kind})")))}\n");
        sb.Append($"features : {(r.Features.Count == 0 ? "(none — pure MVP)" : string.Join(", ", r.Features))}\n");
        if (r.TargetFeatures.Count != 0)
        {
            sb.Append($"target_features: {string.Join(", ", r.TargetFeatures)}\n");
        }
        foreach (var note in r.Notes)
        {
            sb.Append($"note     : {note}\n");
        }
        sb.Append($"opcodes  : {r.InstructionCount} instructions, {r.Opcodes.Count} distinct mnemonics\n");
        // Ranked count desc, then mnemonic asc — the lift surface a future WF2 must cover first.
        foreach (var (mnemonic, count) in r.Opcodes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append($"  {count,5}  {mnemonic}\n");
        }
        return sb.ToString();
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
