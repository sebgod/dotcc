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
internal sealed class CPreprocessor : C.IPreprocessor
{
    private readonly Dictionary<string, LexRule[]> _lexerTable;
    private readonly Dictionary<string, string> _files;
    private readonly Dictionary<string, List<Item>> _macros = new(StringComparer.Ordinal);

    public CPreprocessor(
        Dictionary<string, LexRule[]> lexerTable,
        Dictionary<string, string> files,
        IEnumerable<string> predefines)
    {
        _lexerTable = lexerTable;
        _files = files;
        foreach (var d in predefines)
        {
            // -D NAME or -D NAME=VALUE. Stored body is empty (defined-as-marker)
            // for now — richer expansions need an in-stream lex pass to
            // construct Items; users should write a `#define` inside a header
            // for those cases.
            var eq = d.IndexOf('=');
            var name = eq < 0 ? d : d[..eq];
            _macros[name] = new List<Item>();
        }
    }

    public IEnumerable<Item> OnInclude(IReadOnlyList<Item> args)
    {
        if (args.Count == 0)
        {
            Console.Error.WriteLine("dotcc: #include with no argument");
            return Array.Empty<Item>();
        }
        var raw = (string)args[0].Content;
        if (raw is null || raw.Length < 2 || raw[0] != '"' || raw[^1] != '"')
        {
            Console.Error.WriteLine($"dotcc: #include arg '{raw}' is not a quoted filename");
            return Array.Empty<Item>();
        }
        var name = raw[1..^1];
        if (!_files.TryGetValue(name, out var source))
        {
            Console.Error.WriteLine($"dotcc: #include '{name}' not resolvable (not in -I dirs or system headers)");
            return Array.Empty<Item>();
        }
        using var subLexer = BytesLexer.FromString(source, _lexerTable);
        using var subPreproc = C.WrapPreprocessor(subLexer, this);
        var tokens = new List<Item>();
        while (subPreproc.MoveNext()) { tokens.Add(subPreproc.Current); }
        return tokens;
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
        _macros[name] = args.Count > 1 ? args.Skip(1).ToList() : new List<Item>();
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
        if (token.Content is string text && _macros.TryGetValue(text, out var body))
        {
            return body;
        }
        return new[] { token };
    }

    public bool IsDefined(string name) => name != null && _macros.ContainsKey(name);
}
