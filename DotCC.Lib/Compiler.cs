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
    internal static readonly Dictionary<string, string> SystemHeaders = new(StringComparer.Ordinal)
    {
        ["stdio.h"] = """
            #ifndef _DOTCC_STDIO_H
            #define _DOTCC_STDIO_H
            int printf(char* fmt, ...);
            #endif
            """,
        ["stdlib.h"] = """
            #ifndef _DOTCC_STDLIB_H
            #define _DOTCC_STDLIB_H
            void* malloc(int size);
            void free(void* p);
            #endif
            """,
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
        bool libraryMode = false)
    {
        var includeMap = BuildIncludeMap(inputPaths, includeDirs);
        var emitter = new CSharpEmitter();
        var parser = C.BuildParser(emitter);
        var lexerTable = C.BuildLexer();

        var allFunctions = new StringBuilder();
        var mainArity = -1;
        foreach (var unitPath in inputPaths)
        {
            var source = File.ReadAllText(unitPath);
            var pre = new CPreprocessor(lexerTable, includeMap, defines ?? Array.Empty<string>());
            using var lexer = BytesLexer.FromString(source, lexerTable);
            using var preproc = C.WrapPreprocessor(lexer, pre);
            // TypeNameRewriter: the C lexer hack. Promotes ID → TYPE_NAME for
            // any name previously bound by a `typedef`. Sits AFTER the
            // preprocessor (so macro-expanded tokens are still considered)
            // and BEFORE the LA iterator (so the parser sees the rewritten
            // stream).
            using var typeRewriter = new TypeNameRewriter(preproc);
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
            allFunctions.Append((string)result.Content);

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

        return BuildShell(mainArity, allFunctions.ToString(), emitter.StructDecls, emitter.UsingAliases, fileBased, libraryMode, emitter.Exports);
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
        IReadOnlyList<string>? defines = null)
    {
        var includeMap = BuildIncludeMap(inputPaths, includeDirs);
        var lexerTable = C.BuildLexer();
        foreach (var unitPath in inputPaths)
        {
            output.WriteLine($"# {unitPath}");
            var source = File.ReadAllText(unitPath);
            var pre = new CPreprocessor(lexerTable, includeMap, defines ?? Array.Empty<string>());
            using var lexer = BytesLexer.FromString(source, lexerTable);
            using var preproc = C.WrapPreprocessor(lexer, pre);
            while (preproc.MoveNext())
            {
                var t = preproc.Current;
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
        bool fileBased,
        bool libraryMode,
        IReadOnlyList<CSharpEmitter.Export> exports)
    {
        if (libraryMode)
        {
            return BuildLibraryShell(emittedFnList, structDecls, usingAliases, exports);
        }
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
            using System.Runtime.InteropServices;
            using System.Runtime.CompilerServices;

            // ---- typedef'd `using` aliases (C# 12+ permits `using unsafe X = Y;`
            //      at file scope, ahead of top-level statements). Empty when no
            //      `typedef` declarations were seen.
            {{usingAliases}}
            {{entry}}

            // String literal → pinned UTF-8 RVA pointer. Lives in the
            // assembly's read-only data section, so the pointer is valid for
            // program lifetime — no heap allocation, no GC pinning.
            static unsafe byte* L(ReadOnlySpan<byte> u8) =>
                (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(u8));

            // malloc returns void* (real C semantics); user code casts.
            static unsafe void* Malloc(int size) => NativeMemory.Alloc((nuint)size);
            static unsafe void Free(void* p) => NativeMemory.Free(p);

            // Fluent printf — works around `params object[]` not accepting raw
            // pointers. Each Arg overload consumes the next % spec from the
            // format pointer. The builder is a ref struct so it stays
            // stack-only and zero-alloc.
            static unsafe PrintfBuilder Printf(byte* fmt) => new PrintfBuilder(fmt);


            // ---- user functions (static unsafe local functions) ----

            {{emittedFnList}}

            // ---- type declarations (must come last; C# requires top-level
            //      statements to precede type declarations) ----

            {{structDecls}}

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

            unsafe ref struct PrintfBuilder
            {
                private byte* _fmt;
                public PrintfBuilder(byte* fmt) { _fmt = fmt; }

                // Parsed C printf spec: conversion char + optional flags, width,
                // and precision. -1 means "unspecified" for width/precision.
                // Length modifiers (l/L/h/z) and the '#' alt-form flag are
                // recognized in ParseSpec but ignored at emit time — none of
                // our target outputs depend on them.
                private struct Spec
                {
                    public byte Conv;
                    public int Width;
                    public int Precision;
                    public bool Left;
                    public bool Zero;
                    public bool Plus;
                    public bool Space;
                }

                public PrintfBuilder Arg(int v)
                {
                    var spec = ConsumeUntilSpec();
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    string s;
                    switch (spec.Conv)
                    {
                        case (byte)'d': case (byte)'i':
                            s = v.ToString(ci);
                            if (spec.Plus && v >= 0) { s = "+" + s; }
                            else if (spec.Space && v >= 0) { s = " " + s; }
                            break;
                        case (byte)'x': s = v.ToString("x", ci); break;
                        case (byte)'X': s = v.ToString("X", ci); break;
                        case (byte)'c': Console.Write((char)v); return this;
                        case (byte)'f': case (byte)'e': case (byte)'g':
                            // Integer formatted via the float path — same precision rules apply.
                            s = FormatFloat((double)v, spec, ci);
                            break;
                        default: s = v.ToString(ci); break;
                    }
                    Console.Write(ApplyWidth(s, spec));
                    return this;
                }

                public PrintfBuilder Arg(double v)
                {
                    var spec = ConsumeUntilSpec();
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    string s;
                    switch (spec.Conv)
                    {
                        case (byte)'d': case (byte)'i':
                            s = ((int)v).ToString(ci);
                            break;
                        case (byte)'f': case (byte)'e': case (byte)'g':
                        default:
                            s = FormatFloat(v, spec, ci);
                            break;
                    }
                    Console.Write(ApplyWidth(s, spec));
                    return this;
                }

                public PrintfBuilder Arg(float v) => Arg((double)v);

                public PrintfBuilder Arg(byte* v)
                {
                    var spec = ConsumeUntilSpec();
                    string s;
                    if (spec.Conv == (byte)'s' && v != null)
                    {
                        int len = 0;
                        while (v[len] != 0) { len++; }
                        s = System.Text.Encoding.UTF8.GetString(v, len);
                        // Precision on `%s` caps the string length per C99.
                        if (spec.Precision >= 0 && spec.Precision < s.Length)
                        {
                            s = s[..spec.Precision];
                        }
                    }
                    else if (v == null)
                    {
                        s = "(null)";
                    }
                    else
                    {
                        // System-qualified so a user `typedef int* IntPtr;` (which
                        // lowers to `using unsafe IntPtr = int*;`) doesn't shadow
                        // the BCL type here.
                        s = ((System.IntPtr)v).ToString("X");
                    }
                    Console.Write(ApplyWidth(s, spec));
                    return this;
                }

                public int Done()
                {
                    while (*_fmt != 0)
                    {
                        if (*_fmt == (byte)'%' && _fmt[1] == (byte)'%')
                        {
                            Console.Write('%');
                            _fmt += 2;
                            continue;
                        }
                        WriteUtf8Codepoint(ref _fmt);
                    }
                    return 0;
                }

                private Spec ConsumeUntilSpec()
                {
                    while (*_fmt != 0)
                    {
                        if (*_fmt == (byte)'%')
                        {
                            _fmt++;
                            if (*_fmt == (byte)'%')
                            {
                                Console.Write('%');
                                _fmt++;
                                continue;
                            }
                            return ParseSpec();
                        }
                        WriteUtf8Codepoint(ref _fmt);
                    }
                    return new Spec { Conv = 0, Width = -1, Precision = -1 };
                }

                // Parse `[flags][width][.precision][length]conv` from the byte
                // stream starting just past the leading `%`. Advances `_fmt`
                // through the entire spec, including the conversion char.
                private Spec ParseSpec()
                {
                    var s = new Spec { Width = -1, Precision = -1 };
                    while (*_fmt != 0)
                    {
                        switch (*_fmt)
                        {
                            case (byte)'-': s.Left = true; _fmt++; continue;
                            case (byte)'+': s.Plus = true; _fmt++; continue;
                            case (byte)' ': s.Space = true; _fmt++; continue;
                            case (byte)'0': s.Zero = true; _fmt++; continue;
                            case (byte)'#': _fmt++; continue;
                        }
                        break;
                    }
                    while (*_fmt >= (byte)'0' && *_fmt <= (byte)'9')
                    {
                        if (s.Width < 0) { s.Width = 0; }
                        s.Width = s.Width * 10 + (*_fmt - (byte)'0');
                        _fmt++;
                    }
                    if (*_fmt == (byte)'.')
                    {
                        _fmt++;
                        s.Precision = 0;
                        while (*_fmt >= (byte)'0' && *_fmt <= (byte)'9')
                        {
                            s.Precision = s.Precision * 10 + (*_fmt - (byte)'0');
                            _fmt++;
                        }
                    }
                    while (*_fmt == (byte)'l' || *_fmt == (byte)'L' || *_fmt == (byte)'h' || *_fmt == (byte)'z')
                    {
                        _fmt++;
                    }
                    s.Conv = *_fmt;
                    if (s.Conv != 0) { _fmt++; }
                    return s;
                }

                private static string FormatFloat(double v, Spec spec, System.Globalization.CultureInfo ci)
                {
                    var prec = spec.Precision >= 0 ? spec.Precision : 6;
                    string s = spec.Conv switch
                    {
                        (byte)'e' => v.ToString("E" + prec.ToString(ci), ci),
                        (byte)'g' => spec.Precision >= 0
                            ? v.ToString("G" + prec.ToString(ci), ci)
                            : v.ToString("G", ci),
                        _ => v.ToString("F" + prec.ToString(ci), ci),
                    };
                    if (spec.Plus && v >= 0) { s = "+" + s; }
                    else if (spec.Space && v >= 0) { s = " " + s; }
                    return s;
                }

                private static string ApplyWidth(string s, Spec spec)
                {
                    if (spec.Width <= 0 || s.Length >= spec.Width) { return s; }
                    if (spec.Left) { return s.PadRight(spec.Width, ' '); }
                    // Zero-padding only when right-aligned and no precision (per C).
                    // When the value carries a leading sign, the zero-pad goes
                    // between the sign and the digits — handle that case.
                    if (spec.Zero)
                    {
                        if (s.Length > 0 && (s[0] == '-' || s[0] == '+' || s[0] == ' '))
                        {
                            return s[0] + new string('0', spec.Width - s.Length) + s[1..];
                        }
                        return s.PadLeft(spec.Width, '0');
                    }
                    return s.PadLeft(spec.Width, ' ');
                }

                private static void WriteUtf8Codepoint(ref byte* p)
                {
                    byte b = *p;
                    if (b < 0x80) { Console.Write((char)b); p++; return; }
                    int len = 1;
                    if ((b & 0xE0) == 0xC0) { len = 2; }
                    else if ((b & 0xF0) == 0xE0) { len = 3; }
                    else if ((b & 0xF8) == 0xF0) { len = 4; }
                    Console.Write(System.Text.Encoding.UTF8.GetString(p, len));
                    p += len;
                }
            }
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
        IReadOnlyList<CSharpEmitter.Export> exports)
    {
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
            using System.Runtime.InteropServices;
            using System.Runtime.CompilerServices;

            // ---- typedef'd `using` aliases (same as exe mode).
            {{usingAliases}}
            // User code lives in an internal class so calls between user fns
            // resolve directly (vs. [UnmanagedCallersOnly] methods which can
            // only be invoked via function pointer from C#). External C
            // consumers reach these through the DotCcExports wrappers below.
            internal static class DotCcLib
            {
            {{indentedFns}}

                // Runtime helpers — same shape as the exe shell, promoted to
                // class methods so user code resolves them by bare name
                // within the class scope.
                internal static unsafe byte* L(ReadOnlySpan<byte> u8) =>
                    (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(u8));
                internal static unsafe void* Malloc(int size) => NativeMemory.Alloc((nuint)size);
                internal static unsafe void Free(void* p) => NativeMemory.Free(p);
                internal static unsafe PrintfBuilder Printf(byte* fmt) => new PrintfBuilder(fmt);
            }

            // C-ABI exports. Each wrapper trampoline delegates to the matching
            // DotCcLib method; NativeAOT inlines the trampoline at publish.
            public static class DotCcExports
            {
            {{exportsBlock}}}

            // ---- Type declarations (top-level — same as exe mode). ----
            {{structDecls}}

            // C-truthy → C# bool. Same set of overloads as exe mode.
            static class Cond
            {
                public static bool B(bool b) => b;
                public static bool B(int x) => x != 0;
                public static bool B(double x) => x != 0;
                public static unsafe bool B(void* p) => p != null;
            }

            unsafe ref struct PrintfBuilder
            {
                private byte* _fmt;
                public PrintfBuilder(byte* fmt) { _fmt = fmt; }

                private struct Spec
                {
                    public byte Conv;
                    public int Width;
                    public int Precision;
                    public bool Left;
                    public bool Zero;
                    public bool Plus;
                    public bool Space;
                }

                public PrintfBuilder Arg(int v)
                {
                    var spec = ConsumeUntilSpec();
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    string s;
                    switch (spec.Conv)
                    {
                        case (byte)'d': case (byte)'i':
                            s = v.ToString(ci);
                            if (spec.Plus && v >= 0) { s = "+" + s; }
                            else if (spec.Space && v >= 0) { s = " " + s; }
                            break;
                        case (byte)'x': s = v.ToString("x", ci); break;
                        case (byte)'X': s = v.ToString("X", ci); break;
                        case (byte)'c': Console.Write((char)v); return this;
                        case (byte)'f': case (byte)'e': case (byte)'g':
                            s = FormatFloat((double)v, spec, ci);
                            break;
                        default: s = v.ToString(ci); break;
                    }
                    Console.Write(ApplyWidth(s, spec));
                    return this;
                }

                public PrintfBuilder Arg(double v)
                {
                    var spec = ConsumeUntilSpec();
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    string s;
                    switch (spec.Conv)
                    {
                        case (byte)'d': case (byte)'i':
                            s = ((int)v).ToString(ci);
                            break;
                        case (byte)'f': case (byte)'e': case (byte)'g':
                        default:
                            s = FormatFloat(v, spec, ci);
                            break;
                    }
                    Console.Write(ApplyWidth(s, spec));
                    return this;
                }

                public PrintfBuilder Arg(float v) => Arg((double)v);

                public PrintfBuilder Arg(byte* v)
                {
                    var spec = ConsumeUntilSpec();
                    string s;
                    if (spec.Conv == (byte)'s' && v != null)
                    {
                        int len = 0;
                        while (v[len] != 0) { len++; }
                        s = System.Text.Encoding.UTF8.GetString(v, len);
                        if (spec.Precision >= 0 && spec.Precision < s.Length)
                        {
                            s = s[..spec.Precision];
                        }
                    }
                    else if (v == null) { s = "(null)"; }
                    else { s = ((System.IntPtr)v).ToString("X"); }
                    Console.Write(ApplyWidth(s, spec));
                    return this;
                }

                public int Done()
                {
                    while (*_fmt != 0)
                    {
                        if (*_fmt == (byte)'%' && _fmt[1] == (byte)'%')
                        {
                            Console.Write('%');
                            _fmt += 2;
                            continue;
                        }
                        WriteUtf8Codepoint(ref _fmt);
                    }
                    return 0;
                }

                private Spec ConsumeUntilSpec()
                {
                    while (*_fmt != 0)
                    {
                        if (*_fmt == (byte)'%')
                        {
                            _fmt++;
                            if (*_fmt == (byte)'%')
                            {
                                Console.Write('%');
                                _fmt++;
                                continue;
                            }
                            return ParseSpec();
                        }
                        WriteUtf8Codepoint(ref _fmt);
                    }
                    return new Spec { Conv = 0, Width = -1, Precision = -1 };
                }

                private Spec ParseSpec()
                {
                    var s = new Spec { Width = -1, Precision = -1 };
                    while (*_fmt != 0)
                    {
                        switch (*_fmt)
                        {
                            case (byte)'-': s.Left = true; _fmt++; continue;
                            case (byte)'+': s.Plus = true; _fmt++; continue;
                            case (byte)' ': s.Space = true; _fmt++; continue;
                            case (byte)'0': s.Zero = true; _fmt++; continue;
                            case (byte)'#': _fmt++; continue;
                        }
                        break;
                    }
                    while (*_fmt >= (byte)'0' && *_fmt <= (byte)'9')
                    {
                        if (s.Width < 0) { s.Width = 0; }
                        s.Width = s.Width * 10 + (*_fmt - (byte)'0');
                        _fmt++;
                    }
                    if (*_fmt == (byte)'.')
                    {
                        _fmt++;
                        s.Precision = 0;
                        while (*_fmt >= (byte)'0' && *_fmt <= (byte)'9')
                        {
                            s.Precision = s.Precision * 10 + (*_fmt - (byte)'0');
                            _fmt++;
                        }
                    }
                    while (*_fmt == (byte)'l' || *_fmt == (byte)'L' || *_fmt == (byte)'h' || *_fmt == (byte)'z')
                    {
                        _fmt++;
                    }
                    s.Conv = *_fmt;
                    if (s.Conv != 0) { _fmt++; }
                    return s;
                }

                private static string FormatFloat(double v, Spec spec, System.Globalization.CultureInfo ci)
                {
                    var prec = spec.Precision >= 0 ? spec.Precision : 6;
                    string s = spec.Conv switch
                    {
                        (byte)'e' => v.ToString("E" + prec.ToString(ci), ci),
                        (byte)'g' => spec.Precision >= 0
                            ? v.ToString("G" + prec.ToString(ci), ci)
                            : v.ToString("G", ci),
                        _ => v.ToString("F" + prec.ToString(ci), ci),
                    };
                    if (spec.Plus && v >= 0) { s = "+" + s; }
                    else if (spec.Space && v >= 0) { s = " " + s; }
                    return s;
                }

                private static string ApplyWidth(string s, Spec spec)
                {
                    if (spec.Width <= 0 || s.Length >= spec.Width) { return s; }
                    if (spec.Left) { return s.PadRight(spec.Width, ' '); }
                    if (spec.Zero)
                    {
                        if (s.Length > 0 && (s[0] == '-' || s[0] == '+' || s[0] == ' '))
                        {
                            return s[0] + new string('0', spec.Width - s.Length) + s[1..];
                        }
                        return s.PadLeft(spec.Width, '0');
                    }
                    return s.PadLeft(spec.Width, ' ');
                }

                private static void WriteUtf8Codepoint(ref byte* p)
                {
                    byte b = *p;
                    if (b < 0x80) { Console.Write((char)b); p++; return; }
                    int len = 1;
                    if ((b & 0xE0) == 0xC0) { len = 2; }
                    else if ((b & 0xF0) == 0xE0) { len = 3; }
                    else if ((b & 0xF8) == 0xF0) { len = 4; }
                    Console.Write(System.Text.Encoding.UTF8.GetString(p, len));
                    p += len;
                }
            }
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
