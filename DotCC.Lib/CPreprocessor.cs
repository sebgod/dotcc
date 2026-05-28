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

    // Symbol ids resolved once for the predefined-identifier substitutions
    // in `Rewrite`. `__FILE__` synthesizes a STRING token; `__LINE__`
    // synthesizes a NUM token with the use site's line number.
    private readonly int _numSymbolId;
    private readonly int _stringSymbolId;

    public CPreprocessor(
        Dictionary<string, LexRule[]> lexerTable,
        Dictionary<string, string> files,
        IEnumerable<string> predefines)
    {
        _lexerTable = lexerTable;
        _files = files;
        var symMap = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sym in C.Definition.SymbolNames)
        {
            symMap[sym.Name] = sym.ID;
        }
        _numSymbolId = symMap["NUM"];
        _stringSymbolId = symMap["STRING"];
        foreach (var d in predefines)
        {
            // -D NAME or -D NAME=VALUE. Stored body is empty (defined-as-marker)
            // for now — richer expansions need an in-stream lex pass to
            // construct Items; users should write a `#define` inside a header
            // for those cases.
            var eq = d.IndexOf('=');
            var name = eq < 0 ? d : d[..eq];
            _macros[name] = new MacroDef(name, Params: null, Body: Array.Empty<Item>());
        }
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
        if (!_files.TryGetValue(name, out var source))
        {
            Console.Error.WriteLine($"dotcc: #include '{name}' not resolvable (not in -I dirs or system headers)");
            return Array.Empty<Item>();
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
    /// Resolve the header name from an <c>#include</c>'s collected same-line
    /// args. Accepts two forms: quoted (a single STRING token like
    /// <c>"foo.h"</c>) and angle (a sequence opening with <c>&lt;</c> and
    /// closing with <c>&gt;</c>, fragmented across multiple tokens because
    /// the byte lexer treats angle brackets and dots as separate operators
    /// — we just concatenate the inner token content back into a filename).
    /// </summary>
    private static string? ResolveIncludeName(IReadOnlyList<Item> args)
    {
        if (args.Count == 0)
        {
            Console.Error.WriteLine("dotcc: #include with no argument");
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

        Console.Error.WriteLine($"dotcc: #include arg is not a recognized form (got {args.Count} tokens; expected `\"foo.h\"` or `<foo.h>`)");
        return null;
    }

    public IEnumerable<Item> OnDefine(IReadOnlyList<Item> args)
    {
        if (args.Count < 1)
        {
            Console.Error.WriteLine("dotcc: #define with no name");
            return Array.Empty<Item>();
        }
        var name = (string)args[0].Content;
        if (string.IsNullOrEmpty(name))
        {
            Console.Error.WriteLine("dotcc: #define name is empty");
            return Array.Empty<Item>();
        }

        // Function-like detection: `#define NAME ( … ) body`. We can't
        // distinguish `NAME(x)` from `NAME (x)` here (the byte lexer drops
        // whitespace), so the convention is: any `(` immediately following
        // the name in the directive's args triggers function-like form. The
        // closing `)` ends the parameter list; everything after is the body.
        // A trailing `...` in the param list marks the macro variadic;
        // extra invocation args land in __VA_ARGS__ at expansion.
        if (args.Count >= 2 && args[1].Content?.ToString() == "(")
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
                Console.Error.WriteLine($"dotcc: #define {name}(…) missing closing ')'");
                return Array.Empty<Item>();
            }
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
                return macro.Body;
            }
        }
        return new[] { token };
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
        Console.Error.WriteLine($"dotcc: #warning: {JoinArgs(args)}");
        return Array.Empty<Item>();
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
