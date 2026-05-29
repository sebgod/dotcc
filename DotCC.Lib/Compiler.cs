#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LALR.CC.LexicalGrammar;

namespace DotCC;

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
            map[fileName] = reader.ReadToEnd();
        }
        return map;
    }

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
            sb.AppendLine(StripFileScopeArtifacts(content));
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
            if (trimmed.StartsWith("using ", StringComparison.Ordinal)) { continue; }
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
    };

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
        CDialect? dialect = null)
    {
        var includeMap = BuildIncludeMap(inputPaths, includeDirs);
        var emitter = new CSharpEmitter();
        var parser = C.BuildParser(emitter);
        var lexerTable = C.BuildLexer();
        var activeDialect = dialect ?? CDialect.Default;
        var seededDefines = SeedDialectDefines(activeDialect, defines);

        var allFunctions = new StringBuilder();
        var mainArity = -1;
        foreach (var unitPath in inputPaths)
        {
            var source = File.ReadAllText(unitPath);
            var pre = new CPreprocessor(lexerTable, includeMap, seededDefines);
            pre.SetActiveFilename(Path.GetFileName(unitPath));
            using var lexer = BytesLexer.FromString(source, lexerTable);
            using var preproc = C.WrapPreprocessor(lexer, pre);
            // MacroExpander: function-like macro expansion. Needs lookahead
            // for the `(`, which the Rewrite hook can't do — so it lives as
            // its own RewritingTokenStream subclass after the preprocessor
            // populated the macro table.
            using var macroExp = new MacroExpander(preproc, pre);
            // DialectKeywordRewriter: dialect-aware keyword promotion (rule 2
            // of the gating model). Promotes identifier-spelled keywords
            // (e.g. C23 `bool`) onto their grammar terminal only when the
            // active dialect makes them first-class. Sits AFTER macro
            // expansion (so an included header's `#define bool _Bool` wins and
            // the table simply doesn't fire) and BEFORE the typedef rewriter
            // (so e.g. `typedef bool MyBool;` under c23 sees `_Bool`).
            using var dialectRewriter = new DialectKeywordRewriter(macroExp, activeDialect);
            // TypeNameRewriter: the C lexer hack. Promotes ID → TYPE_NAME for
            // any name previously bound by a `typedef`. Sits AFTER macro
            // expansion (so expanded names can also trigger typedef
            // rewrites) and BEFORE the LA iterator. Seeded with the small
            // set of C#-side libc classes that user code reaches by name
            // through synthetic-header typedefs (e.g. <setjmp.h>'s
            // `typedef LongJmpToken jmp_buf;`).
            using var typeRewriter = new TypeNameRewriter(dialectRewriter, PredefinedTypeNames);
            using var tokens = new SyncLATokenIterator(typeRewriter);

            Item result;
            try
            {
                result = parser.ParseInput(tokens, debugger: null, trimReductions: true);
            }
            catch (global::LALR.CC.ParseErrorException ex)
            {
                throw new CompileException($"parse failed in {Path.GetFileName(unitPath)}: {ex.Message}", ex);
            }
            if (result.IsError)
            {
                throw new CompileException($"parse failed in {Path.GetFileName(unitPath)}: {result}");
            }

            if (allFunctions.Length > 0) { allFunctions.AppendLine(); }
            // The visitor's IVisitor<EmitContent> returns a discriminated
            // union; for the top-level FnList the result is always a Text
            // variant carrying the concatenated function emit string.
            allFunctions.Append(((EmitContent.Text)result.Content).Value);

            if (emitter.MainArity >= 0)
            {
                mainArity = emitter.MainArity;
                emitter.ResetMainArity();
            }
        }

        // Library mode doesn't need a `main` — the produced .dll is consumed
        // through its [UnmanagedCallersOnly] exports. Exe mode still requires
        // one entry point to dispatch to.
        if (!libraryMode && mainArity < 0)
        {
            throw new CompileException("no `main` function defined in any translation unit.");
        }

        return BuildShell(mainArity, allFunctions.ToString(), emitter.StructDecls, emitter.UsingAliases, emitter.Globals, fileBased, libraryMode, emitter.Exports);
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
            var source = File.ReadAllText(unitPath);
            var pre = new CPreprocessor(lexerTable, includeMap, seededDefines);
            pre.SetActiveFilename(Path.GetFileName(unitPath));
            using var lexer = BytesLexer.FromString(source, lexerTable);
            using var preproc = C.WrapPreprocessor(lexer, pre);
            // -E mode also routes through MacroExpander so function-like
            // macro expansion is visible in the dumped token stream.
            using var macroExp = new MacroExpander(preproc, pre);
            while (macroExp.MoveNext())
            {
                var t = macroExp.Current;
                output.Write(t.Content is string s ? s : t.Content?.ToString());
                output.Write(' ');
            }
            output.WriteLine();
        }
    }

    /// <summary>
    /// Build the csproj scaffold paired with the non-file-based shell from
    /// <see cref="EmitCSharp"/>. The frontend exe writes both into the output
    /// dir for the default csproj/build modes. When <paramref name="libraryMode"/>
    /// is true, configures <c>NativeLib=Shared</c> + <c>PublishAot=true</c>
    /// so <c>dotnet publish</c> produces a C-callable native shared library
    /// (<c>.dll</c> / <c>.so</c> / <c>.dylib</c>).
    /// </summary>
    public static string BuildGeneratedCsproj(bool libraryMode = false)
    {
        if (libraryMode)
        {
            return """
                <Project Sdk="Microsoft.NET.Sdk">
                  <!-- Generated by dotcc (library mode, -shared). -->
                  <PropertyGroup>
                    <OutputType>Library</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <RootNamespace>DotCCGenerated</RootNamespace>
                    <AssemblyName>dotcc-out</AssemblyName>
                    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                    <Nullable>disable</Nullable>
                    <!-- NativeAOT shared-library knobs. Producing the actual
                         .so/.dll/.dylib requires `dotnet publish`. -->
                    <PublishAot>true</PublishAot>
                    <NativeLib>Shared</NativeLib>
                    <IsTrimmable>true</IsTrimmable>
                  </PropertyGroup>
                </Project>
                """;
        }
        return """
            <Project Sdk="Microsoft.NET.Sdk">
              <!-- Generated by dotcc. -->
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <RootNamespace>DotCCGenerated</RootNamespace>
                <AssemblyName>dotcc-out</AssemblyName>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <Nullable>disable</Nullable>
              </PropertyGroup>
            </Project>
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
    private static string[] SeedDialectDefines(CDialect dialect, IReadOnlyList<string>? userDefines)
    {
        var seeded = new List<string>(4 + (userDefines?.Count ?? 0))
        {
            "__STDC__=1",
            "__STDC_HOSTED__=1",
            // Compiler identification — analogous to clang's `__clang__`
            // and gcc's `__GNUC__`. User code can `#ifdef __dotcc__` to
            // branch on the compiler identity in mixed-source projects.
            "__dotcc__=1",
        };
        var stdcVersion = dialect.StdcVersionLiteral;
        if (stdcVersion is not null)
        {
            seeded.Add($"__STDC_VERSION__={stdcVersion}");
        }
        if (userDefines is not null) { seeded.AddRange(userDefines); }
        return seeded.ToArray();
    }

    private static Dictionary<string, string> BuildIncludeMap(
        IReadOnlyList<string> inputPaths,
        IReadOnlyList<string>? includeDirs)
    {
        // Resolve headers: scan every -I directory + every .h alongside each .c
        // + synthetic system headers. Last-wins so user -I overrides system.
        var includeMap = new Dictionary<string, string>(SystemHeaders, StringComparer.Ordinal);
        var dirs = (includeDirs ?? Array.Empty<string>())
            .Concat(inputPaths.Select(p => Path.GetDirectoryName(Path.GetFullPath(p)) ?? "."))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) { continue; }
            foreach (var hpath in Directory.EnumerateFiles(dir, "*.h"))
            {
                includeMap[Path.GetFileName(hpath)] = File.ReadAllText(hpath);
            }
        }
        return includeMap;
    }

    internal static string BuildShell(
        int mainArity,
        string emittedFnList,
        string structDecls,
        string usingAliases,
        string globals,
        bool fileBased,
        bool libraryMode,
        IReadOnlyList<CSharpEmitter.Export> exports)
    {
        if (libraryMode)
        {
            return BuildLibraryShell(emittedFnList, structDecls, usingAliases, globals, exports);
        }
        // Embedded DotCC.Libc runtime block — spliced into the heredoc
        // below so the emitted .cs is self-contained even without a
        // <PackageReference Include="DotCC.Libc"> in scope.
        var runtimeBlock = _runtimeBlock.Value;
        var header = fileBased ? "#:property AllowUnsafeBlocks=true\n\n" : string.Empty;
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
                    byte** argv = (byte**)NativeMemory.Alloc((nuint)argc * (nuint)sizeof(byte*));
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
                    try
                    {
                        return main(argc, argv);
                    }
                    finally
                    {
                        for (int i = 0; i < argc; i++) { NativeMemory.Free(argv[i]); }
                        NativeMemory.Free(argv);
                    }
                }
                """,
            _ => throw new InvalidOperationException(
                $"dotcc: `main` must have 0, 1, or 2 parameters; got {mainArity}."),
        };

        return $$"""
            {{header}}// <auto-generated>
            // Emitted by dotcc from c.lalr.yaml + the input translation units.
            // </auto-generated>
            using System;
            using System.Globalization;
            using System.IO;
            using System.Runtime.InteropServices;
            using System.Runtime.CompilerServices;
            using System.Text;
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
            // ---- File-scope variables ---------------------------------
            // C globals live as static fields of a `DotCcGlobals` class
            // (declared at file end). `using static DotCcGlobals;` makes
            // them visible by bare name in every function — C local
            // functions can't capture top-level locals because they're
            // emitted as `static`, hence the class indirection.
            using static DotCcGlobals;

            // ---- typedef'd `using` aliases (C# 12+ permits `using unsafe X = Y;`
            //      at file scope, ahead of top-level statements). Empty when no
            //      `typedef` declarations were seen.
            {{usingAliases}}
            {{entry}}


            // ---- user functions (static unsafe local functions) ----

            {{emittedFnList}}

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
        IReadOnlyList<CSharpEmitter.Export> exports)
    {
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
            if (e.Params.Contains("params object[]", StringComparison.Ordinal))
            {
                exportsBlock.Append($"    // dotcc: '{e.Name}' has varargs — not exported (no UnmanagedCallersOnly support).\n");
                continue;
            }
            var argNames = ExtractArgNames(e.Params);
            exportsBlock.Append($"    [UnmanagedCallersOnly(EntryPoint = \"{e.Name}\", CallConvs = new[] {{ typeof(CallConvCdecl) }})]\n");
            exportsBlock.Append($"    public static unsafe {e.ReturnType} {e.Name}({e.Params}) => DotCcLib.{e.Name}({argNames});\n\n");
        }

        return $$"""
            // <auto-generated>
            // Emitted by dotcc from c.lalr.yaml + the input translation units.
            // Library mode (-shared) — NativeAOT-publishable shared lib with
            // C-ABI exports via [UnmanagedCallersOnly].
            // </auto-generated>
            using System;
            using System.Globalization;
            using System.IO;
            using System.Runtime.InteropServices;
            using System.Runtime.CompilerServices;
            using System.Text;
            using static Libc;
            using static DotCcGlobals;

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
/// Thrown by <see cref="Compiler.EmitCSharp"/> on parse failure or when no
/// <c>main</c> is defined across the supplied translation units. The
/// frontend exe catches and maps to a non-zero exit code with a clang-shaped
/// diagnostic; tests assert on the message.
/// </summary>
public sealed class CompileException : Exception
{
    public CompileException(string message) : base(message) { }
    public CompileException(string message, Exception inner) : base(message, inner) { }
}
