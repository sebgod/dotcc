#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LALR.CC;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>
/// <c>IPreprocessor</c> impl. Owns the macro table that <c>#define</c>
/// populates, <c>#undef</c> mutates, and which <see cref="Rewrite"/> +
/// <see cref="IsDefined"/> consult. Resolves <c>#include</c> through the
/// compile's <see cref="Compiler.IncludeResolver"/> (the includer's directory,
/// the <c>-I</c> directories, the embedded system headers).
/// </summary>
/// <summary>
/// One macro definition. Object-like macros have <see cref="Params"/> = null
/// and a body that's substituted verbatim at the use site. Function-like
/// macros carry their formal parameter list AND a body — invocation requires
/// matching <c>(</c> at the use site followed by comma-separated arg
/// expressions; <see cref="MacroExpander"/> does that paren-balanced collection
/// + per-parameter substitution. Variadic macros (<c>#define LOG(fmt, ...)</c>)
/// set <see cref="IsVariadic"/> — extra args beyond the formal list bind to
/// the magic name <c>__VA_ARGS__</c> at substitution time.
/// </summary>
internal sealed record MacroDef(
    string Name,
    IReadOnlyList<string>? Params,
    IReadOnlyList<Item> Body,
    bool IsVariadic = false)
{
    public bool IsFunctionLike => Params is not null;
}

internal sealed class CPreprocessor : C.IPreprocessor
{
    private readonly Dictionary<string, LexRule[]> _lexerTable;
    private readonly Compiler.IncludeResolver _resolver;
    private readonly System.IO.TextWriter _diag;
    private readonly Dictionary<string, MacroDef> _macros = new(StringComparer.Ordinal);
    // The file the preprocessor is currently processing (the top-level
    // translation unit OR a recursive `#include`), three ways:
    // `_currentlyIncluding` is its name as spelled (what `__FILE__` reports),
    // `_currentKey` its identity (what `#pragma once` records) and `_currentDir`
    // its directory (where a quoted `#include` inside it looks first; null for
    // an embedded header). The top-level values are set by `SetActiveFile`
    // before processing; OnInclude saves and restores them around its
    // recursive sub-preprocess so nested includes work correctly.
    private string? _currentlyIncluding;
    private string? _currentKey;
    private string? _currentDir;
    // `#line` remapping (C89 §6.10.4). `#line N` makes the line FOLLOWING the
    // directive logical line N, so __LINE__ on a token at physical line `phys`
    // reports `phys + _lineDelta`. `#line N "file"` also overrides __FILE__ via
    // _fileOverride — kept SEPARATE from the current file's identity, which
    // #pragma once and -MD dependency tracking key on (its real path). Both
    // are per-file: OnInclude saves/resets/restores them around a recursive
    // #include so an includer's remap can't bleed into the included file (and
    // vice versa). CHEAP IMPLEMENTATION — these feed only the user-observable
    // __LINE__/__FILE__ macros; downstream diagnostics (parse errors,
    // DialectGate, CompileException) still report PHYSICAL positions. Making
    // those follow #line means threading the remap through the whole SrcPos
    // chain downstream of the preprocessor — deferred. See C-SUPPORT.md's #line row.
    private int _lineDelta;
    private string? _fileOverride;
    private readonly HashSet<string> _pragmaOnceFiles = new(StringComparer.Ordinal);
    // Multiple-include optimization. After processing a file for the first
    // time we scan its raw text for the standard header-guard wrapping
    // pattern (`#ifndef X / #define X / ... / #endif` with nothing
    // meaningful outside the guard) and cache `filename → X`. On the next
    // `#include` of the same name, if `X` is still defined we short-circuit
    // without opening + lexing the file again. Same optimization gcc and
    // clang document as "controlling macro" detection — it's what lets
    // real codebases include the same header transitively from a hundred
    // translation units without paying lex cost each time. A value of
    // `null` in this map means "we've examined the file and it has no
    // controlling guard" — we still re-process it on each include, just
    // don't re-scan.
    private readonly Dictionary<string, string?> _fileGuards = new(StringComparer.Ordinal);
    // Diagnostic counter: number of `#include` calls that short-circuited
    // via the multiple-include optimization. Exposed for tests so the
    // optimization can be asserted observable without resorting to timing.
    internal int IncludeOptimizationHits { get; private set; }

    // Dependency tracking for -MD/-MMD depfiles: every disk file actually
    // `#include`d (transitively — recursive includes share this instance), in
    // first-seen order, paired with whether it arrived via the angle `<...>`
    // (system) or quoted `"..."` form. The angle flag is what -MMD keys on to
    // drop system headers. Embedded headers have no path and are not recorded
    // (nothing for a build tool to stat). Populated in OnInclude; read by
    // Compiler.EmitDependencyRule after draining the stream.
    private readonly List<(string Path, bool IsSystem)> _includedHeaders = new();
    private readonly HashSet<string> _includeSeen = new(StringComparer.Ordinal);
    internal IReadOnlyList<(string Path, bool IsSystem)> IncludedHeaders => _includedHeaders;

    // Symbol ids resolved once for the predefined identifiers (`__FILE__`
    // synthesizes a STRING token, `__LINE__` a NUM token) and for the `#if`
    // operators `defined` / `__has_include` / `__has_embed`.
    private readonly int _numSymbolId;
    private readonly int _stringSymbolId;
    private readonly int _idSymbolId;
    // Synthetic terminal for C23 #embed. OnEmbed emits one Item of this symbol
    // per directive, carrying the content-hash key into _embeds (no lexer rule —
    // the terminal is produced here, never scanned). See c.lalr.yaml's EMBED.
    private readonly int _embedSymbolId;

    // C23 #embed support. _embedDirs are the filesystem directories probed for an
    // embedded file (the -I dirs + each TU's own dir); _embeds is the byte
    // side-table OnEmbed writes and IrBuilder.BuildEmbed reads back, keyed by the
    // content hash stamped on the carrier token (so identical embeds dedup).
    private readonly IReadOnlyList<string> _embedDirs;
    private readonly Dictionary<string, byte[]> _embeds;

    // Dialect-gating sink for preprocessor-era features (variadic macros C99,
    // #warning C23). Non-null only on the emit pass under -pedantic — null on
    // the analysis pass and the default path, so each gate is a no-op and a
    // violation is collected once. See DialectGate.
    private readonly DialectGate? _gate;

    public CPreprocessor(
        Dictionary<string, LexRule[]> lexerTable,
        Compiler.IncludeResolver resolver,
        IEnumerable<string> predefines,
        bool quiet = false,
        DialectGate? gate = null,
        IReadOnlyList<string>? embedDirs = null,
        Dictionary<string, byte[]>? embeds = null)
    {
        _lexerTable = lexerTable;
        _resolver = resolver;
        _gate = gate;
        _embedDirs = embedDirs ?? Array.Empty<string>();
        _embeds = embeds ?? new Dictionary<string, byte[]>(StringComparer.Ordinal);
        // Diagnostics sink. The analysis (first) pass of the two-pass emit runs
        // quiet so its #warning / #include-resolution messages don't print
        // twice; the real emit pass uses stderr as usual. (A fatal #error still
        // throws in either pass — see OnError.)
        _diag = quiet ? System.IO.TextWriter.Null : Console.Error;
        var symMap = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sym in C.Definition.SymbolNames)
        {
            symMap[sym.Name] = sym.ID;
        }
        _numSymbolId = symMap["NUM"];
        _stringSymbolId = symMap["STRING"];
        _idSymbolId = symMap["ID"];
        _embedSymbolId = symMap["EMBED"];
        Macros = new MacroEngine(this);
        foreach (var d in predefines)
        {
            // -D NAME           → defined-as-marker (empty body)
            // -D NAME=VALUE     → lex VALUE through the same byte lexer the
            //                     parser uses, so the substitution is a real
            //                     token sequence. This is what makes
            //                     `__STDC_VERSION__=201710L` work as the
            //                     LHS of `#if __STDC_VERSION__ >= 199901L`
            //                     (EvaluateCondition macro-replaces the
            //                     condition first). It also makes user-supplied
            //                     `-D X=42` substitute as `42` at use site
            //                     instead of disappearing.
            var eq = d.IndexOf('=');
            var name = eq < 0 ? d : d[..eq];
            var body = eq < 0 || eq == d.Length - 1
                ? Array.Empty<Item>()
                : LexMacroValue(d[(eq + 1)..]);
            _macros[name] = new MacroDef(name, Params: null, Body: body);
        }
    }

    /// <summary>
    /// Lex a <c>-D NAME=VALUE</c> right-hand side into the matching token
    /// sequence. Whitespace is dropped by the grammar's lexer rules, so
    /// <c>"1 + 2"</c> yields three tokens just like an in-source body.
    /// Returns empty on lex failure rather than throwing — the frontend
    /// already validated <c>-D</c> at parse time.
    /// </summary>
    private Item[] LexMacroValue(string text)
    {
        using var lex = Compiler.LexC(text, _lexerTable);
        var list = new List<Item>();
        while (lex.MoveNext()) { list.Add(lex.Current); }
        return list.ToArray();
    }

    /// <summary>
    /// Public accessor used by <see cref="MacroExpander"/> to consult the
    /// macro table during expansion. Returns false for both undefined names
    /// and names defined as function-like-but-without-args at the call site
    /// (the latter is the expander's call to resolve).
    /// </summary>
    public bool TryGetMacro(string name, out MacroDef macro)
        => _macros.TryGetValue(name, out macro!);

    /// <summary>
    /// Set the active translation unit: its name as <c>__FILE__</c> reports it,
    /// its identity for <c>#pragma once</c>, and its directory, where a quoted
    /// <c>#include</c> looks first. Called by <see cref="Compiler.EmitCSharp"/> /
    /// <see cref="Compiler.Preprocess"/> at the start of each translation unit.
    /// <c>#include</c>'s recursive drive overwrites these around the nested
    /// processing (then restores).
    /// </summary>
    public void SetActiveFile(string path)
    {
        // A library unit (`<libc>/sin.c`) is no disk file: its directory is the marker that
        // sends its includes to the library's headers.
        if (path.StartsWith(Compiler.IncludeResolver.LibraryDir + "/", StringComparison.Ordinal))
        {
            _currentlyIncluding = path[(Compiler.IncludeResolver.LibraryDir.Length + 1)..];
            _currentKey = path;
            _currentDir = Compiler.IncludeResolver.LibraryDir;
            return;
        }
        var full = System.IO.Path.GetFullPath(path);
        _currentlyIncluding = System.IO.Path.GetFileName(path);
        _currentKey = full;
        _currentDir = System.IO.Path.GetDirectoryName(full);
    }

    /// <summary>
    /// <c>#include "name"</c> / <c>#include &lt;name&gt;</c>: resolve the file
    /// (<see cref="Compiler.IncludeResolver"/>), then preprocess and macro-expand
    /// it in place. A name that resolves to no file is a fatal error, as in every
    /// C compiler: carrying on would only surface later as an unexpanded macro or
    /// an undeclared name far from the cause.
    /// </summary>
    public IEnumerable<Item> OnInclude(IReadOnlyList<Item> args)
    {
        var name = ResolveIncludeName(args, out var isSystem);
        var line = args.Count > 0 ? args[0].Position.Line : 0;
        if (name is null)
        {
            throw new CompileException(
                $"{_currentlyIncluding}:{line}: error: #include expects \"FILENAME\" or <FILENAME>");
        }
        if (_resolver.Resolve(name, isSystem, _currentDir) is not { } file)
        {
            throw new CompileException(
                $"{_currentlyIncluding}:{line}: fatal error: '{name}' file not found");
        }
        if (file.IsSynthetic) { _resolver.IncludedBuiltins.Add(name); }
        // Dependency tracking (-MD/-MMD): record every disk file once, in
        // first-seen order, BEFORE the pragma-once / include-guard
        // short-circuits below, so a header that's pulled in many times is
        // still listed.
        if (_includeSeen.Add(file.Key) && file.Path is { } diskPath)
        {
            _includedHeaders.Add((diskPath, isSystem));
        }
        if (_pragmaOnceFiles.Contains(file.Key))
        {
            // Already processed via `#pragma once` — drop the include body.
            return Array.Empty<Item>();
        }
        // Multiple-include optimization: if a previous include of this same
        // file detected the standard header-guard wrapping pattern and the
        // guard macro is still defined, the file is guaranteed to expand
        // to nothing useful — skip opening + lexing entirely.
        if (_fileGuards.TryGetValue(file.Key, out var cachedGuard)
            && cachedGuard is not null
            && _macros.ContainsKey(cachedGuard))
        {
            IncludeOptimizationHits++;
            return Array.Empty<Item>();
        }
        if (!_resolver.TryRead(file, name, out var source, out var readError))
        {
            throw new CompileException(
                $"{_currentlyIncluding}:{line}: fatal error: '{name}' could not be read: {readError}");
        }
        // First-time include of this file: scan the source text for a
        // controlling header guard. Cache the result (or null) so the
        // detection cost is paid at most once per file.
        if (!_fileGuards.ContainsKey(file.Key))
        {
            _fileGuards[file.Key] = DetectControllingMacro(source);
        }
        var saved = _currentlyIncluding;
        var savedKey = _currentKey;
        var savedDir = _currentDir;
        // A #line remap is per-file: the included file starts fresh (physical
        // line 1, its own presumed name), so reset on entry and restore the
        // includer's remap on exit.
        var savedLineDelta = _lineDelta;
        var savedFileOverride = _fileOverride;
        _currentlyIncluding = name;
        _currentKey = file.Key;
        _currentDir = file.Directory;
        _lineDelta = 0;
        _fileOverride = null;
        try
        {
            // Lex a synthetic system header in a reserved line band so every
            // prototype it declares lands at Line >= SyntheticLineBase, flagging
            // it FromSystemHeader (runtime-provided — never an `-l` import
            // candidate). A user `-I` header that shares a synthetic name is
            // found first and stays at line 1, like every user header and `.c`
            // splice.
            var initialLine = file.IsSynthetic
                ? Ir.SrcPos.SyntheticLineBase
                : 1;
            using var subLexer = Compiler.LexC(source, _lexerTable, initialLine);
            using var subPreproc = WrapPreprocessor(subLexer);
            // Expand function-like macros WITHIN the include, mirroring the
            // top-level pipeline (where MacroExpander sits above the preprocessor).
            // Without this, a macro the included file both DEFINES and #undefs —
            // chibi's param.c is `#define _I(x) … / use(s) / #undef _I` — is
            // removed from the table by its own trailing #undef while this
            // OnInclude is still draining the body, so the single downstream
            // MacroExpander (which only sees the already-drained include) finds
            // `_I` undefined and leaves `_I(…)` raw → CS0103 in the emit. Expanding
            // here, while the definition is still live, matches how the top-level
            // #define→use→#undef token stream lazily expands each use before the
            // #undef is reached. (A file that defines without undefining — e.g.
            // opcodes.c's `_I` — worked either way; this fixes the undef case.)
            // Each token leaves as an ExpandedItem so the includer's expander
            // (which sees them only after the whole file, #defines and all, has
            // been processed) does not expand them a second time.
            using var subMacro = new MacroExpander(subPreproc, this);
            var tokens = new List<Item>();
            while (subMacro.MoveNext())
            {
                var t = subMacro.Current;
                tokens.Add(t as ExpandedItem ?? new ExpandedItem(t));
            }
            return tokens;
        }
        finally
        {
            _currentlyIncluding = saved;
            _currentKey = savedKey;
            _currentDir = savedDir;
            _lineDelta = savedLineDelta;
            _fileOverride = savedFileOverride;
        }
    }

    /// <summary>
    /// C23 <c>#embed "file"</c> / <c>#embed &lt;file&gt;</c>. Reads the named
    /// file's RAW bytes and emits exactly ONE synthetic <c>EMBED</c> token whose
    /// content is a content-hash key into the byte side-table — deliberately NOT
    /// the standard's comma-separated list of integer constants, which for a
    /// multi-MB file would explode into millions of tokens. The single carrier
    /// rides the existing initializer-list productions and is expanded to byte
    /// constants in the IR (<c>IrBuilder.ParseInitList</c>), where a
    /// <c>const char[]</c> target lowers to a zero-copy RVA blob.
    /// </summary>
    /// <remarks>
    /// V1 handles the simple <c>"name"</c> / <c>&lt;name&gt;</c> forms; the
    /// parameter clauses (<c>limit(N)</c> / <c>if_empty(…)</c> / <c>prefix(…)</c>
    /// / <c>suffix(…)</c>) warn and are ignored. Search path mirrors
    /// <c>#include</c>: the <c>-I</c> dirs + the TU's own directory, first-wins.
    /// </remarks>
    public IEnumerable<Item> OnEmbed(IReadOnlyList<Item> args)
    {
        if (args.Count == 0)
        {
            _diag.WriteLine("dotcc: #embed with no argument");
            return Array.Empty<Item>();
        }
        var name = ResolveEmbedName(args, out var paramStart);
        if (name is null) { return Array.Empty<Item>(); }
        ParseEmbedParams(args, paramStart, name, out var limit, out var ifEmpty);
        var path = ResolveEmbedPath(name);
        if (path is null)
        {
            _diag.WriteLine($"dotcc: #embed '{name}' not found (searched -I dirs and the source directory)");
            return Array.Empty<Item>();
        }
        byte[] bytes;
        try { bytes = System.IO.File.ReadAllBytes(path); }
        catch (Exception ex)
        {
            _diag.WriteLine($"dotcc: #embed '{name}' could not be read: {ex.Message}");
            return Array.Empty<Item>();
        }
        // limit(N): embed at most N leading bytes (C23); limit(0) → empty resource.
        if (limit is { } lim && bytes.Length > lim) { bytes = bytes[..lim]; }
        // An empty resource (0-byte file or limit(0)) expands to the if_empty
        // replacement tokens when given, else to nothing (C23 §6.10.3.2).
        if (bytes.Length == 0) { return ifEmpty ?? Array.Empty<Item>(); }
        // Key by content hash: identical embeds across TUs share one side-table
        // entry, and the key is stable + collision-free without a global counter.
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        _embeds[key] = bytes;
        return new[] { new Item(_embedSymbolId, key, args[0].Position) };
    }

    /// <summary>Extract the embed filename from its directive args (quoted or
    /// angle form, like <c>#include</c>), reporting the index where the trailing
    /// parameter clauses begin (<c>args.Count</c> when there are none).</summary>
    private string? ResolveEmbedName(IReadOnlyList<Item> args, out int paramStart)
    {
        paramStart = args.Count;
        // Quoted: `#embed "f"` [params…]
        if (args[0].Content is string raw && raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            paramStart = 1;
            return raw[1..^1];
        }
        // Angle: `#embed <f>` [params…] — find the first '>' (the filename close).
        if (args.Count >= 2 && args[0].Content is string open && open == "<")
        {
            var close = -1;
            for (var i = 1; i < args.Count; i++)
            {
                if (args[i].Content as string == ">") { close = i; break; }
            }
            if (close < 0)
            {
                _diag.WriteLine("dotcc: #embed angle form missing closing '>'");
                return null;
            }
            paramStart = close + 1;
            var sb = new System.Text.StringBuilder();
            for (var i = 1; i < close; i++) { sb.Append(args[i].Content?.ToString()); }
            return sb.ToString();
        }
        _diag.WriteLine("dotcc: #embed arg is not a recognized form (expected \"file\" or <file>)");
        return null;
    }

    /// <summary>Parse the C23 <c>#embed</c> parameter clauses (each
    /// <c>name ( tokens… )</c>) following the filename. V1 honors <c>limit(N)</c>
    /// (a non-negative integer literal) and <c>if_empty(tokens)</c> (substituted
    /// when the resource is empty); <c>prefix</c>/<c>suffix</c> and any vendor
    /// parameter warn and are ignored.</summary>
    private void ParseEmbedParams(
        IReadOnlyList<Item> args, int start, string name, out int? limit, out IReadOnlyList<Item>? ifEmpty)
    {
        limit = null;
        ifEmpty = null;
        var i = start;
        while (i < args.Count)
        {
            var pname = args[i].Content as string;
            if (pname is null || i + 1 >= args.Count || args[i + 1].Content as string != "(")
            {
                _diag.WriteLine($"dotcc: #embed '{name}': unrecognized parameter near '{pname}' — ignored");
                return;
            }
            // Collect the balanced token run between the clause's parentheses.
            var depth = 0;
            var inner = new List<Item>();
            var j = i + 1;
            for (; j < args.Count; j++)
            {
                var t = args[j].Content as string;
                if (t == "(") { depth++; if (depth == 1) { continue; } }
                else if (t == ")") { depth--; if (depth == 0) { j++; break; } }
                inner.Add(args[j]);
            }
            switch (pname)
            {
                case "limit":
                    if (inner.Count == 1 && long.TryParse(inner[0].Content?.ToString(), out var n) && n >= 0)
                    {
                        limit = (int)Math.Min(n, int.MaxValue);
                    }
                    else
                    {
                        _diag.WriteLine($"dotcc: #embed '{name}': limit(...) expects a non-negative integer literal — ignored");
                    }
                    break;
                case "if_empty":
                    ifEmpty = inner;
                    break;
                case "prefix":
                case "suffix":
                    _diag.WriteLine($"dotcc: #embed '{name}': {pname}(...) is not yet supported — ignored");
                    break;
                default:
                    _diag.WriteLine($"dotcc: #embed '{name}': unknown parameter '{pname}' — ignored");
                    break;
            }
            i = j;
        }
    }

    /// <summary>Probe the embed search dirs (then the name as-is) for an existing
    /// file, returning the first hit's path or null — the same first-wins search
    /// order as <c>#include</c>.</summary>
    private string? ResolveEmbedPath(string name)
    {
        foreach (var dir in _embedDirs)
        {
            var p = System.IO.Path.Combine(dir, name);
            if (System.IO.File.Exists(p)) { return p; }
        }
        return System.IO.File.Exists(name) ? name : null;
    }

    /// <summary>Evaluate <c>__has_embed("file")</c> (C23) for a <c>#if</c>: the
    /// resource-status constant — <c>__STDC_EMBED_NOT_FOUND__</c> (0) when the
    /// file doesn't resolve, <c>__STDC_EMBED_EMPTY__</c> (2) when it resolves but
    /// is empty, else <c>__STDC_EMBED_FOUND__</c> (1). (V1 ignores embed
    /// parameters in the probe; bare existence + emptiness is the common case.)</summary>
    private int EvalHasEmbed(IReadOnlyList<Item> argTokens)
    {
        if (argTokens.Count == 0) { return 0; }
        var name = ResolveEmbedName(argTokens, out _);
        if (name is null) { return 0; }
        var path = ResolveEmbedPath(name);
        if (path is null) { return 0; }
        try { return new System.IO.FileInfo(path).Length == 0 ? 2 : 1; }
        catch { return 0; }
    }

    /// <summary>Evaluate <c>__has_include("hdr")</c> / <c>&lt;hdr&gt;</c> (C23 /
    /// long-standing extension) for a <c>#if</c>: 1 when the header resolves the
    /// way <c>#include</c> would resolve it here, else 0.</summary>
    private bool EvalHasInclude(IReadOnlyList<Item> argTokens)
    {
        if (argTokens.Count == 0) { return false; }
        var name = ResolveIncludeName(argTokens, out var isSystem);
        return name is not null && _resolver.Resolve(name, isSystem, _currentDir) is not null;
    }

    /// <summary>
    /// Scan <paramref name="source"/> for the standard header-guard
    /// wrapping pattern:
    /// <code>
    /// #ifndef NAME
    /// #define NAME
    /// ... body ...
    /// #endif
    /// </code>
    /// Returns <c>NAME</c> if the file matches that shape exactly (only
    /// comments + whitespace are allowed outside the outer guard), else
    /// <c>null</c>. This is the same "controlling macro" detection gcc
    /// and clang use to short-circuit subsequent includes — the speedup
    /// matters in real projects where the same header gets transitively
    /// pulled in from dozens of TUs.
    /// </summary>
    /// <remarks>
    /// Operates on the raw source text rather than the lexer's tokens
    /// because (a) directives need a different tokenisation than C
    /// statements and (b) we want this to run BEFORE the recursive
    /// preprocess, so it can decide whether to even enter that recursion.
    /// </remarks>
    internal static string? DetectControllingMacro(string source)
    {
        var i = SkipWhitespaceAndComments(source, 0);
        if (!MatchDirective(source, ref i, "ifndef")) { return null; }
        i = SkipHSpace(source, i);
        if (!ReadIdent(source, ref i, out var guardName)) { return null; }
        i = SkipWhitespaceAndComments(source, i);
        if (!MatchDirective(source, ref i, "define")) { return null; }
        i = SkipHSpace(source, i);
        if (!ReadIdent(source, ref i, out var defineName)) { return null; }
        if (guardName != defineName) { return null; }

        // Scan the body, tracking nesting depth. We've just consumed the
        // outer #ifndef → depth starts at 1. Find the matching #endif
        // (depth back to 0) and verify nothing meaningful follows it.
        var depth = 1;
        var endifEnd = -1;
        while (i < source.Length && depth > 0)
        {
            i = SkipWhitespaceAndComments(source, i);
            if (i >= source.Length) { break; }
            // Any '#' at this point IS a directive — SkipWhitespaceAndComments
            // already advanced past comments, and intra-line content (string
            // literals, code) doesn't contain a stray '#' that'd land here at
            // a line-leading position. We just peek the directive name.
            if (source[i] == '#')
            {
                i++;
                i = SkipHSpace(source, i);
                if (!ReadIdent(source, ref i, out var directive))
                {
                    // `#` followed by nothing — skip the line, keep scanning.
                    i = SkipToEndOfLine(source, i);
                    continue;
                }
                switch (directive)
                {
                    case "if":
                    case "ifdef":
                    case "ifndef":
                        depth++;
                        break;
                    case "endif":
                        depth--;
                        if (depth == 0) { endifEnd = i; }
                        else if (depth < 0) { return null; }
                        break;
                    // `#else` / `#elif` don't change nesting; everything else
                    // (#define, #include, #pragma, #error, #warning, #undef,
                    // #line, ...) is just regular body content.
                }
                i = SkipToEndOfLine(source, i);
            }
            else
            {
                // Non-directive line — skip to next.
                i = SkipToEndOfLine(source, i);
            }
        }
        if (endifEnd < 0) { return null; }

        // Only whitespace / comments allowed after the closing #endif.
        var tail = SkipWhitespaceAndComments(source, endifEnd);
        if (tail < source.Length) { return null; }
        return guardName;
    }

    private static int SkipWhitespaceAndComments(string s, int i)
    {
        while (i < s.Length)
        {
            var c = s[i];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
            if (c == '/' && i + 1 < s.Length)
            {
                if (s[i + 1] == '/')
                {
                    // Line comment runs to newline.
                    i += 2;
                    while (i < s.Length && s[i] != '\n') { i++; }
                    continue;
                }
                if (s[i + 1] == '*')
                {
                    // Block comment runs to */.
                    i += 2;
                    while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) { i++; }
                    if (i + 1 < s.Length) { i += 2; }
                    else { i = s.Length; }
                    continue;
                }
            }
            break;
        }
        return i;
    }

    private static int SkipHSpace(string s, int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) { i++; }
        return i;
    }

    private static int SkipToEndOfLine(string s, int i)
    {
        while (i < s.Length && s[i] != '\n') { i++; }
        if (i < s.Length) { i++; }
        return i;
    }

    /// <summary>
    /// Try to match a preprocessor directive (<c># [space*] keyword</c>)
    /// at position <paramref name="i"/>. Advances <paramref name="i"/>
    /// past the directive keyword on success.
    /// </summary>
    private static bool MatchDirective(string s, ref int i, string keyword)
    {
        var start = i;
        if (i >= s.Length || s[i] != '#') { return false; }
        i++;
        i = SkipHSpace(s, i);
        if (i + keyword.Length > s.Length) { i = start; return false; }
        for (var k = 0; k < keyword.Length; k++)
        {
            if (s[i + k] != keyword[k]) { i = start; return false; }
        }
        var after = i + keyword.Length;
        // Reject identifier continuation: `#ifndefined` is not `#ifndef`.
        if (after < s.Length && (char.IsLetterOrDigit(s[after]) || s[after] == '_'))
        {
            i = start;
            return false;
        }
        i = after;
        return true;
    }

    private static bool ReadIdent(string s, ref int i, out string name)
    {
        var start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) { i++; }
        name = s[start..i];
        return name.Length > 0;
    }

    /// <summary>
    /// Resolve the header name from an <c>#include</c>'s collected same-line
    /// args. Accepts two forms: quoted (a single STRING token like
    /// <c>"foo.h"</c>) and angle (a sequence opening with <c>&lt;</c> and
    /// closing with <c>&gt;</c>, fragmented across multiple tokens because
    /// the byte lexer treats angle brackets and dots as separate operators
    /// — we just concatenate the inner token content back into a filename).
    /// </summary>
    /// <remarks><paramref name="isSystem"/> reports the angle form, which
    /// <c>-MMD</c> uses to drop system headers from the dependency file.</remarks>
    private string? ResolveIncludeName(IReadOnlyList<Item> args, out bool isSystem)
    {
        isSystem = false;
        if (args.Count == 0)
        {
            _diag.WriteLine("dotcc: #include with no argument");
            return null;
        }

        // Quoted form: `#include "foo.h"` — single STRING token.
        if (args.Count == 1 && args[0].Content is string raw
            && raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            return raw[1..^1];
        }

        // Angle form: `#include <stdio.h>` — bracketed sequence of tokens.
        if (args.Count >= 2
            && args[0].Content is string open && open == "<"
            && args[^1].Content is string close && close == ">")
        {
            isSystem = true;
            var sb = new System.Text.StringBuilder();
            for (var i = 1; i < args.Count - 1; i++)
            {
                sb.Append(args[i].Content?.ToString());
            }
            return sb.ToString();
        }

        _diag.WriteLine($"dotcc: #include arg is not a recognized form (got {args.Count} tokens; expected `\"foo.h\"` or `<foo.h>`)");
        return null;
    }

    public IEnumerable<Item> OnDefine(IReadOnlyList<Item> args)
    {
        if (args.Count < 1)
        {
            _diag.WriteLine("dotcc: #define with no name");
            return Array.Empty<Item>();
        }
        var name = (string)args[0].Content;
        if (string.IsNullOrEmpty(name))
        {
            _diag.WriteLine("dotcc: #define name is empty");
            return Array.Empty<Item>();
        }

        // Function-like detection: `#define NAME(args) body` is function-
        // like ONLY when the `(` is immediately adjacent to NAME with no
        // intervening whitespace. `#define NAME (args) body` (with space)
        // is object-like with body `(args) body`. The byte lexer drops
        // whitespace, but it preserves token positions — we check that
        // the `(` token's column is exactly NAME.column + NAME.length.
        // The closing `)` ends the parameter list; everything after is
        // the body. A trailing `...` in the param list marks the macro
        // variadic; extra invocation args land in __VA_ARGS__ at expansion.
        if (args.Count >= 2 && args[1].Content?.ToString() == "(" && IsAdjacent(args[0], args[1]))
        {
            var paramNames = new List<string>();
            var isVariadic = false;
            var pos = 2;
            while (pos < args.Count && args[pos].Content?.ToString() != ")")
            {
                var tokenText = args[pos].Content?.ToString();
                if (tokenText == "...")
                {
                    isVariadic = true;
                }
                else if (tokenText is { } p && p != ",")
                {
                    paramNames.Add(p);
                }
                pos++;
            }
            if (pos >= args.Count)
            {
                _diag.WriteLine($"dotcc: #define {name}(…) missing closing ')'");
                return Array.Empty<Item>();
            }
            // Variadic macros (`#define LOG(fmt, ...)`) are a C99 feature.
            if (isVariadic) { _gate?.RequireMin(1999, "variadic macro", args[0].Position.Line); }
            var body = args.Skip(pos + 1).ToList();
            _macros[name] = new MacroDef(name, paramNames, body, isVariadic);
            return Array.Empty<Item>();
        }

        // Object-like: body is everything after the name.
        var objBody = args.Count > 1 ? args.Skip(1).ToList() : new List<Item>();
        _macros[name] = new MacroDef(name, Params: null, Body: objBody);
        return Array.Empty<Item>();
    }

    public IEnumerable<Item> OnUndef(IReadOnlyList<Item> args)
    {
        if (args.Count > 0 && args[0].Content is string name)
        {
            _macros.Remove(name);
        }
        return Array.Empty<Item>();
    }

    /// <summary>
    /// The <see cref="C.IPreprocessor"/> per-token rewrite hook, a pass-through:
    /// macro replacement needs a function-like macro's unexpanded arguments and
    /// the tokens after a replacement, so it all happens in
    /// <see cref="MacroEngine"/> (driven by <see cref="MacroExpander"/> for text,
    /// <see cref="EvaluateCondition"/> for <c>#if</c>). <see cref="WrapPreprocessor"/>
    /// does not install this hook at all.
    /// </summary>
    public IEnumerable<Item> Rewrite(Item token) => new[] { token };

    /// <summary>The macro engine over this preprocessor's macro table.</summary>
    internal MacroEngine Macros { get; }

    /// <summary>The name of the file being processed, for diagnostics.</summary>
    internal string DiagnosticFile => _fileOverride ?? _currentlyIncluding ?? "<input>";

    /// <summary>
    /// Wrap <paramref name="lexer"/> in the directive / conditional stage: the
    /// directives dispatch here, ordinary text passes through untouched (a
    /// <see cref="MacroExpander"/> above it does the replacing), and <c>#if</c> /
    /// <c>#elif</c> conditions are evaluated by <see cref="EvaluateCondition"/>.
    /// </summary>
    internal PreprocessorTokenStream WrapPreprocessor(ISyncIterator<Item> lexer)
        => new(lexer, C.BuildPreprocessor(this), rewrite: null, C.BuildConditionals(this))
        {
            EvaluateCondition = EvaluateCondition,
        };

    /// <summary>
    /// The value of a predefined identifier: <c>__LINE__</c> as a NUM (physical
    /// <paramref name="line"/> shifted by any <c>#line</c>) and <c>__FILE__</c> as
    /// a STRING; null for any other token.
    /// </summary>
    internal Item? PredefinedValue(Item token, int line)
    {
        switch (token.Content as string)
        {
            case "__LINE__":
                var logical = (line + _lineDelta).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new Item(_numSymbolId, logical, token.Position);
            case "__FILE__":
                // A `#line N "file"` filename override wins over the file being
                // processed. STRING tokens carry the raw lexeme, quotes included.
                var name = _fileOverride ?? _currentlyIncluding ?? string.Empty;
                return new Item(_stringSymbolId, "\"" + name + "\"", token.Position);
            default:
                return null;
        }
    }

    /// <summary>Lex <paramref name="text"/> into preprocessing tokens (the result
    /// of a <c>##</c> paste is whatever its spelling lexes to).</summary>
    internal IReadOnlyList<Item> LexPreprocessingTokens(string text) => LexMacroValue(text);

    /// <summary>
    /// Evaluate a <c>#if</c> / <c>#elif</c> condition (C11 6.10.1). The operators
    /// that look at names rather than values go first, on the tokens as written:
    /// <c>defined NAME</c> / <c>defined(NAME)</c>, <c>__has_include(…)</c> and
    /// <c>__has_embed(…)</c> become 1 or 0 (or the embed status). The rest is then
    /// macro-replaced like any other text, and the result evaluated as an integer
    /// constant expression (identifiers left over are 0).
    /// </summary>
    internal bool EvaluateCondition(IReadOnlyList<Item> args)
    {
        var tokens = new List<Item>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            var t = args[i];
            if (t.ID == _idSymbolId && t.Content is string name)
            {
                if (name == "defined")
                {
                    var (operand, end) = DefinedOperand(args, i);
                    tokens.Add(new Item(_numSymbolId, IsDefined(operand) ? "1" : "0", t.Position));
                    i = end;
                    continue;
                }
                if (name is "__has_include" or "__has_embed"
                    && i + 1 < args.Count && (args[i + 1].Content as string) == "(")
                {
                    var inner = new List<Item>();
                    var depth = 0;
                    var j = i + 1;
                    for (; j < args.Count; j++)
                    {
                        var c = args[j].Content as string;
                        if (c == "(" && depth++ == 0) { continue; }
                        if (c == ")" && --depth == 0) { break; }
                        inner.Add(args[j]);
                    }
                    var value = name == "__has_include" ? (EvalHasInclude(inner) ? 1 : 0) : EvalHasEmbed(inner);
                    tokens.Add(new Item(_numSymbolId, value.ToString(System.Globalization.CultureInfo.InvariantCulture), t.Position));
                    i = j;
                    continue;
                }
            }
            tokens.Add(t);
        }
        return PreprocessorExpressionEvaluator.Evaluate(DecimalIntegers(Macros.ExpandList(tokens)), IsDefined);
    }

    /// <summary>
    /// <paramref name="tokens"/> with each integer constant spelled in plain decimal, its
    /// suffix gone: <c>#if</c> evaluates every one in intmax_t or uintmax_t whatever its suffix
    /// (C11 6.10.1p4), and the evaluator reads only decimal. Without this <c>0xFFu</c> read as
    /// no number, and CPython's <c>#if STRINGLIB_MAX_CHAR &gt; 0x7Fu</c> dropped every
    /// <c>ucs*lib_count</c>. A constant past 64 bits is left for the evaluator to reject.
    /// </summary>
    private List<Item> DecimalIntegers(IEnumerable<Item> tokens)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var result = new List<Item>();
        foreach (var t in tokens)
        {
            result.Add(t.ID == _numSymbolId && t.Content is string num && EmitHelpers.TryParseIntConstant(num, out var value)
                ? new Item(_numSymbolId, value.ToString(inv), t.Position)
                : t);
        }
        return result;
    }

    /// <summary>The name <c>defined</c> at <paramref name="at"/> tests, as
    /// <c>defined NAME</c> or <c>defined ( NAME )</c>, and the index of the last
    /// token of the operator.</summary>
    private (string Name, int End) DefinedOperand(IReadOnlyList<Item> args, int at)
    {
        if (at + 1 < args.Count && args[at + 1].ID == _idSymbolId && args[at + 1].Content is string bare)
        {
            return (bare, at + 1);
        }
        if (at + 3 < args.Count && (args[at + 1].Content as string) == "("
            && args[at + 2].ID == _idSymbolId && args[at + 2].Content is string inner
            && (args[at + 3].Content as string) == ")")
        {
            return (inner, at + 3);
        }
        throw new CompileException($"{DiagnosticFile}:{args[at].Position.Line}: error: operator \"defined\" requires an identifier");
    }

    public bool IsDefined(string name) => name != null && _macros.ContainsKey(name);

    /// <summary>
    /// <c>#pragma</c> dispatcher. Currently only <c>#pragma once</c> is
    /// honoured — the rest are silently ignored (matches the convention of
    /// most compilers: unknown pragmas don't break the build).
    /// </summary>
    public IEnumerable<Item> OnPragma(IReadOnlyList<Item> args)
    {
        if (args.Count > 0 && args[0].Content is string s && s == "once")
        {
            // Remember the currently-being-processed file as include-once.
            // Any subsequent #include that resolves to the same file (by any
            // spelling) short-circuits in OnInclude.
            if (_currentKey is not null)
            {
                _pragmaOnceFiles.Add(_currentKey);
            }
        }
        return Array.Empty<Item>();
    }

    /// <summary>
    /// <c>#line N</c> / <c>#line N "file"</c> (C89 §6.10.4). Renumbers the lines
    /// that FOLLOW the directive: the next physical line becomes logical line
    /// <paramref name="args"/>[0] (<c>N</c>), and an optional string literal
    /// overrides the presumed filename.
    /// </summary>
    /// <remarks>
    /// CHEAP IMPLEMENTATION — this honours the directive only for the two
    /// user-observable macros it feeds: <c>__LINE__</c> (via <c>_lineDelta</c>)
    /// and <c>__FILE__</c> (via <c>_fileOverride</c>). Compiler diagnostics —
    /// parse errors, <see cref="DialectGate"/>, <see cref="CompileException"/> —
    /// still report the PHYSICAL line/file; making them follow <c>#line</c> would
    /// mean threading the remap through the whole <c>Ir.SrcPos</c> chain
    /// downstream of the preprocessor, which is deferred (see the <c>#line</c>
    /// row in C-SUPPORT.md). The operands are object-like-macro-expanded first,
    /// per the standard; a malformed directive is reported and ignored rather
    /// than fatal.
    /// </remarks>
    public IEnumerable<Item> OnLine(IReadOnlyList<Item> args)
    {
        if (args.Count == 0)
        {
            _diag.WriteLine("dotcc: #line: expected a line number");
            return Array.Empty<Item>();
        }
        // The directive sits on this physical line (its first argument token's
        // line). Macro expansion below may pull body tokens in from elsewhere
        // (carrying the definition's position), so capture the use-site line FIRST.
        var directivePhysLine = args[0].Position.Line;
        // The standard says the arguments are macro-expanded before
        // interpretation (`#define LN 100` then `#line LN`).
        var expanded = Macros.ExpandList(args);
        if (expanded.Count == 0 || expanded[0].Content is not string numText
            || !int.TryParse(numText, System.Globalization.NumberStyles.None,
                             System.Globalization.CultureInfo.InvariantCulture, out var logical)
            || logical < 1)
        {
            _diag.WriteLine("dotcc: #line: expected a positive line number");
            return Array.Empty<Item>();
        }
        // `#line N` makes the FOLLOWING physical line (directivePhysLine + 1)
        // logical line N, so a token at physical `phys` reports
        // `phys + (N - directivePhysLine - 1)`.
        _lineDelta = logical - directivePhysLine - 1;
        // Optional filename: a string literal token (carries its own quotes).
        if (expanded.Count > 1 && expanded[1].Content is string fileText
            && fileText.Length >= 2 && fileText[0] == '"' && fileText[^1] == '"')
        {
            _fileOverride = fileText[1..^1];
        }
        return Array.Empty<Item>();
    }

    /// <summary>
    /// <c>#error msg</c>: abort compilation with the joined message text, as a
    /// fatal diagnostic naming the file and line the way clang and gcc report it
    /// (<c>npy_cpu.h:122: error: #error Unknown CPU…</c>). Visible to callers as
    /// a <see cref="CompileException"/>, same as a parse failure.
    /// </summary>
    public IEnumerable<Item> OnError(IReadOnlyList<Item> args)
    {
        var where = args.Count > 0
            ? $"{_currentlyIncluding}:{args[0].Position.Line}: "
            : $"{_currentlyIncluding}: ";
        throw new CompileException($"{where}error: #error {JoinArgs(args)}");
    }

    /// <summary>
    /// <c>#warning msg</c> — emit a diagnostic to stderr and continue.
    /// </summary>
    public IEnumerable<Item> OnWarning(IReadOnlyList<Item> args)
    {
        // `#warning` was a long-standing extension, standardized in C23.
        _gate?.RequireMin(2023, "#warning directive", args.Count > 0 ? args[0].Position.Line : 0);
        _diag.WriteLine($"dotcc: #warning: {JoinArgs(args)}");
        return Array.Empty<Item>();
    }

    /// <summary>
    /// Are <paramref name="left"/> and <paramref name="right"/> tokens
    /// adjacent in the source — i.e., no whitespace between them?
    /// Used by <see cref="OnDefine"/> to distinguish function-like
    /// (<c>NAME(args)</c>, no space) from object-like with parenthesized
    /// body (<c>NAME (expr)</c>, space). Both tokens must be on the
    /// same line; the right token's column must equal left's column
    /// plus left's text length.
    /// </summary>
    /// <summary>Whether <paramref name="right"/> followed <paramref name="left"/>
    /// with no white space between them in the source.</summary>
    internal static bool IsAdjacent(Item left, Item right)
    {
        if (left.Position.Line != right.Position.Line) { return false; }
        var leftText = left.Content?.ToString() ?? string.Empty;
        return right.Position.Column == left.Position.Column + leftText.Length;
    }

    private static string JoinArgs(IReadOnlyList<Item> args)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0) { sb.Append(' '); }
            sb.Append(args[i].Content?.ToString());
        }
        return sb.ToString();
    }
}
