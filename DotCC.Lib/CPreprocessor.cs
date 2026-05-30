#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>
/// <c>IPreprocessor</c> impl. Owns the macro table that <c>#define</c>
/// populates, <c>#undef</c> mutates, and which <see cref="Rewrite"/> +
/// <see cref="IsDefined"/> consult. Resolves <c>#include</c> against a shared
/// header map (system + user headers).
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
    private readonly Dictionary<string, string> _files;
    private readonly System.IO.TextWriter _diag;
    private readonly Dictionary<string, MacroDef> _macros = new(StringComparer.Ordinal);
    // `#pragma once` machinery + active filename for `__FILE__`. The same
    // `_currentlyIncluding` field tracks both: it names the file the
    // preprocessor is currently processing (the top-level translation unit
    // OR a recursive `#include`). The top-level value is set by
    // `SetActiveFilename` from Compiler.EmitCSharp before processing; the
    // OnInclude handler saves+restores around its recursive sub-preprocess
    // so nested includes work correctly.
    private string? _currentlyIncluding;
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

    // Symbol ids resolved once for the predefined-identifier substitutions
    // in `Rewrite`. `__FILE__` synthesizes a STRING token; `__LINE__`
    // synthesizes a NUM token with the use site's line number.
    private readonly int _numSymbolId;
    private readonly int _stringSymbolId;

    // Dialect-gating sink for preprocessor-era features (variadic macros C99,
    // #warning C23). Non-null only on the emit pass under -pedantic — null on
    // the analysis pass and the default path, so each gate is a no-op and a
    // violation is collected once. See DialectGate.
    private readonly DialectGate? _gate;

    public CPreprocessor(
        Dictionary<string, LexRule[]> lexerTable,
        Dictionary<string, string> files,
        IEnumerable<string> predefines,
        bool quiet = false,
        DialectGate? gate = null)
    {
        _lexerTable = lexerTable;
        _files = files;
        _gate = gate;
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
        foreach (var d in predefines)
        {
            // -D NAME           → defined-as-marker (empty body)
            // -D NAME=VALUE     → lex VALUE through the same byte lexer the
            //                     parser uses, so the substitution is a real
            //                     token sequence. This is what makes
            //                     `__STDC_VERSION__=201710L` work as the
            //                     LHS of `#if __STDC_VERSION__ >= 199901L`
            //                     (the conditional-expression evaluator
            //                     pre-expands object-like macros via
            //                     Rewrite). It also makes user-supplied
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
        using var lex = BytesLexer.FromString(text, _lexerTable);
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
    /// Set the active source filename for <c>__FILE__</c> expansion. Called
    /// by <see cref="Compiler.EmitCSharp"/> / <see cref="Compiler.Preprocess"/>
    /// at the start of each translation unit. <c>#include</c>'s recursive
    /// drive overwrites this around the nested processing (then restores).
    /// </summary>
    public void SetActiveFilename(string name) => _currentlyIncluding = name;

    public IEnumerable<Item> OnInclude(IReadOnlyList<Item> args)
    {
        var name = ResolveIncludeName(args);
        if (name is null) { return Array.Empty<Item>(); }
        if (_pragmaOnceFiles.Contains(name))
        {
            // Already processed via `#pragma once` — drop the include body.
            return Array.Empty<Item>();
        }
        // Multiple-include optimization: if a previous include of this same
        // file detected the standard header-guard wrapping pattern and the
        // guard macro is still defined, the file is guaranteed to expand
        // to nothing useful — skip opening + lexing entirely.
        if (_fileGuards.TryGetValue(name, out var cachedGuard)
            && cachedGuard is not null
            && _macros.ContainsKey(cachedGuard))
        {
            IncludeOptimizationHits++;
            return Array.Empty<Item>();
        }
        if (!_files.TryGetValue(name, out var source))
        {
            _diag.WriteLine($"dotcc: #include '{name}' not resolvable (not in -I dirs or system headers)");
            return Array.Empty<Item>();
        }
        // First-time include of this file: scan the source text for a
        // controlling header guard. Cache the result (or null) so the
        // detection cost is paid at most once per filename.
        if (!_fileGuards.ContainsKey(name))
        {
            _fileGuards[name] = DetectControllingMacro(source);
        }
        var saved = _currentlyIncluding;
        _currentlyIncluding = name;
        try
        {
            using var subLexer = BytesLexer.FromString(source, _lexerTable);
            using var subPreproc = C.WrapPreprocessor(subLexer, this);
            var tokens = new List<Item>();
            while (subPreproc.MoveNext()) { tokens.Add(subPreproc.Current); }
            return tokens;
        }
        finally
        {
            _currentlyIncluding = saved;
        }
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
    private string? ResolveIncludeName(IReadOnlyList<Item> args)
    {
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

    public IEnumerable<Item> Rewrite(Item token)
    {
        // Predefined identifiers come first — they shadow any same-named
        // user macro by C standard (which forbids redefining them anyway).
        if (token.Content is string text)
        {
            if (text == "__LINE__")
            {
                var line = token.Position.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new[] { new Item(_numSymbolId, line, token.Position) };
            }
            if (text == "__FILE__")
            {
                var name = _currentlyIncluding ?? string.Empty;
                // STRING tokens carry the raw lexeme including surrounding
                // quotes — the visitor's Str() strips them and re-wraps in
                // the u8 literal form.
                return new[] { new Item(_stringSymbolId, "\"" + name + "\"", token.Position) };
            }
            // Function-like macros need lookahead (peek for the `(`) which the
            // Rewrite hook can't do — MacroExpander handles those downstream.
            // Object-like still expands here so the existing -E (Preprocess-only)
            // mode keeps working without wiring MacroExpander into that pipeline.
            if (_macros.TryGetValue(text, out var macro) && !macro.IsFunctionLike)
            {
                // Rescan the expansion so chained macros like
                //   #define UCHAR_MAX 255
                //   #define CHAR_MAX  UCHAR_MAX
                // transitively resolve at use site. Hide set guards
                // against self-referential cycles (`#define A A`).
                var hideSet = new HashSet<string>(StringComparer.Ordinal) { text };
                return ExpandObjectLikeBody(macro.Body, hideSet);
            }
        }
        return new[] { token };
    }

    /// <summary>
    /// Rescan a macro body for further object-like substitutions. Each
    /// token whose text matches a known object-like macro NOT in the
    /// hide set gets replaced by its body, which is itself recursively
    /// rescanned. The hide set propagates outward to the caller's name
    /// so a macro can't expand to itself (per the C standard's
    /// "hideset" rule).
    /// </summary>
    private List<Item> ExpandObjectLikeBody(IReadOnlyList<Item> body, HashSet<string> hideSet)
    {
        var result = new List<Item>(body.Count);
        foreach (var item in body)
        {
            if (item.Content is string text
                && !hideSet.Contains(text)
                && _macros.TryGetValue(text, out var inner)
                && !inner.IsFunctionLike)
            {
                var nestedHide = new HashSet<string>(hideSet, StringComparer.Ordinal) { text };
                result.AddRange(ExpandObjectLikeBody(inner.Body, nestedHide));
            }
            else
            {
                result.Add(item);
            }
        }
        return result;
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
            // Any subsequent #include of the same filename short-circuits in
            // OnInclude.
            if (_currentlyIncluding is not null)
            {
                _pragmaOnceFiles.Add(_currentlyIncluding);
            }
        }
        return Array.Empty<Item>();
    }

    /// <summary>
    /// <c>#error msg</c> — abort compilation with the joined message text.
    /// Visible to callers as a <see cref="CompileException"/>, same as a
    /// parse failure.
    /// </summary>
    public IEnumerable<Item> OnError(IReadOnlyList<Item> args)
    {
        throw new CompileException($"#error: {JoinArgs(args)}");
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
    private static bool IsAdjacent(Item left, Item right)
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
