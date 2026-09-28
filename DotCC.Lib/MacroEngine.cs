#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using LALR.CC;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>
/// The set of macro names a preprocessing token may no longer be replaced by
/// (C11 6.10.3.4p2): the names of the macros whose replacement produced it. A
/// token whose own name is in its hide set is "painted blue" and stays unexpanded
/// wherever it travels afterwards, including through another macro's arguments.
/// Immutable; the names are kept sorted so the small sets macro expansion builds
/// stay cheap to test, extend, join and intersect.
/// </summary>
internal sealed class HideSet
{
    /// <summary>The hide set of every token read from the source.</summary>
    public static readonly HideSet Empty = new(Array.Empty<string>());

    private readonly string[] _names;

    private HideSet(string[] names) { _names = names; }

    /// <summary>Whether no macro name is hidden.</summary>
    public bool IsEmpty => _names.Length == 0;

    /// <summary>Whether <paramref name="name"/> is hidden.</summary>
    public bool Contains(string name) => Array.BinarySearch(_names, name, StringComparer.Ordinal) >= 0;

    /// <summary>This set plus <paramref name="name"/>.</summary>
    public HideSet With(string name)
    {
        var at = Array.BinarySearch(_names, name, StringComparer.Ordinal);
        if (at >= 0) { return this; }
        at = ~at;
        var names = new string[_names.Length + 1];
        Array.Copy(_names, 0, names, 0, at);
        names[at] = name;
        Array.Copy(_names, at, names, at + 1, _names.Length - at);
        return new HideSet(names);
    }

    /// <summary>The names in either set.</summary>
    public HideSet Union(HideSet other)
    {
        if (other.IsEmpty || ReferenceEquals(this, other)) { return this; }
        if (IsEmpty) { return other; }
        var merged = new List<string>(_names.Length + other._names.Length);
        int i = 0, j = 0;
        while (i < _names.Length || j < other._names.Length)
        {
            var c = i == _names.Length ? 1
                : j == other._names.Length ? -1
                : string.CompareOrdinal(_names[i], other._names[j]);
            if (c <= 0) { merged.Add(_names[i++]); if (c == 0) { j++; } }
            else { merged.Add(other._names[j++]); }
        }
        return merged.Count == _names.Length ? this
            : merged.Count == other._names.Length ? other
            : new HideSet(merged.ToArray());
    }

    /// <summary>The names in both sets.</summary>
    public HideSet Intersect(HideSet other)
    {
        if (IsEmpty || other.IsEmpty) { return Empty; }
        if (ReferenceEquals(this, other)) { return this; }
        var common = new List<string>(Math.Min(_names.Length, other._names.Length));
        int i = 0, j = 0;
        while (i < _names.Length && j < other._names.Length)
        {
            var c = string.CompareOrdinal(_names[i], other._names[j]);
            if (c == 0) { common.Add(_names[i]); i++; j++; }
            else if (c < 0) { i++; }
            else { j++; }
        }
        return common.Count == 0 ? Empty
            : common.Count == _names.Length ? this
            : common.Count == other._names.Length ? other
            : new HideSet(common.ToArray());
    }
}

/// <summary>A preprocessing token produced by macro replacement, carrying its
/// <see cref="HideSet"/>.</summary>
internal sealed class HiddenItem : Item
{
    /// <summary>Copy <paramref name="token"/> (symbol, content, position) with
    /// the hide set <paramref name="hide"/>.</summary>
    public HiddenItem(Item token, HideSet hide) : base(token.ID, token.Content, token.Position)
    {
        Hide = hide;
    }

    /// <summary>The macro names this token can no longer be replaced by.</summary>
    public HideSet Hide { get; }
}

/// <summary>
/// C macro replacement (C11 6.10.3), one engine for every context: ordinary text
/// (driven token by token by <see cref="MacroExpander"/>), macro arguments (the
/// argument prescan), <c>#if</c> / <c>#elif</c> conditions and <c>#line</c>. It is
/// Prosser's hide-set algorithm, the model the standard's rescanning rules
/// describe: every token carries the set of macro names it may no longer be
/// replaced by; replacing an object-like macro <c>M</c> gives its tokens
/// <c>HS(M) ∪ {M}</c>, a function-like invocation <c>(HS(M) ∩ HS(')')) ∪ {M}</c>;
/// and a replacement list is pushed back in front of the remaining input, so the
/// rescan sees what follows it (a function-like name at the end of a replacement
/// can take its arguments from the source, <c>f(1)(2)</c>). Arguments are
/// collected unexpanded and fully expanded on their own before substitution,
/// except as operands of <c>#</c> and <c>##</c>, which see the spelling as written.
/// </summary>
internal sealed class MacroEngine
{
    private const string VaArgsName = "__VA_ARGS__";
    private const string VaOptName = "__VA_OPT__";

    private readonly CPreprocessor _cpp;
    private readonly int _idSymbol;
    private readonly int _openParenSymbol;
    private readonly int _closeParenSymbol;
    private readonly int _commaSymbol;
    private readonly int _stringSymbol;
    private readonly int _hashSymbol;
    private readonly int _hashHashSymbol;

    /// <summary>Resolve the token symbols the engine matches on.</summary>
    public MacroEngine(CPreprocessor cpp)
    {
        _cpp = cpp;
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sym in C.Definition.SymbolNames)
        {
            map[sym.Name] = sym.ID;
        }
        _idSymbol = map["ID"];
        _openParenSymbol = map["("];
        _closeParenSymbol = map[")"];
        _commaSymbol = map[","];
        _stringSymbol = map["STRING"];
        _hashSymbol = map["#"];
        _hashHashSymbol = map["##"];
    }

    /// <summary>Reads the next token from the input that follows a
    /// <see cref="Source"/>'s pushed-back tokens (the upstream token stream).</summary>
    public delegate bool Reader([NotNullWhen(true)] out Item? item);

    /// <summary>
    /// The input of one expansion: tokens a replacement pushed back come first,
    /// then, for ordinary text, the upstream stream. A source without an upstream
    /// is an isolated token list (an argument prescan, a condition).
    /// </summary>
    public sealed class Source
    {
        private readonly Stack<Item> _pending = new();
        private readonly Reader? _upstream;

        /// <summary>A source over <paramref name="upstream"/> (null: pushed-back
        /// tokens only).</summary>
        public Source(Reader? upstream) { _upstream = upstream; }

        /// <summary>Push <paramref name="tokens"/> back in front of the input,
        /// in order.</summary>
        public void PushFront(IReadOnlyList<Item> tokens)
        {
            for (var i = tokens.Count - 1; i >= 0; i--) { _pending.Push(tokens[i]); }
        }

        /// <summary>The next pushed-back token, never reading the upstream.</summary>
        public bool TryTakePending([NotNullWhen(true)] out Item? token)
        {
            if (_pending.Count > 0) { token = _pending.Pop(); return true; }
            token = null;
            return false;
        }

        /// <summary>The next token, reading the upstream once the pushed-back
        /// tokens run out.</summary>
        public bool TryNext([NotNullWhen(true)] out Item? token)
        {
            if (_pending.Count > 0) { token = _pending.Pop(); return true; }
            if (_upstream is { } read && read(out var next)) { token = next; return true; }
            token = null;
            return false;
        }

        /// <summary>The next token without consuming it (an upstream token read
        /// to answer is kept as pushed back).</summary>
        public bool TryPeek([NotNullWhen(true)] out Item? token)
        {
            if (_pending.Count > 0) { token = _pending.Peek(); return true; }
            if (_upstream is { } read && read(out var next)) { _pending.Push(next); token = next; return true; }
            token = null;
            return false;
        }
    }

    /// <summary>The hide set of <paramref name="token"/> (empty for a source token).</summary>
    private static HideSet HideOf(Item token) => token is HiddenItem h ? h.Hide : HideSet.Empty;

    /// <summary>
    /// Replace macros starting at <paramref name="first"/>, sending every final
    /// token to <paramref name="emit"/> in order. Rescanning consumes only what the
    /// replacements pushed back; the upstream is read just far enough to find a
    /// function-like invocation's <c>(</c> and arguments, so ordinary text streams.
    /// </summary>
    public void Run(Item first, Source source, Action<Item> emit)
    {
        var anchorLine = first.Position.Line;
        source.PushFront(new[] { first });
        while (source.TryTakePending(out var token))
        {
            Step(token, source, emit, anchorLine);
        }
    }

    /// <summary>Fully macro-replace <paramref name="tokens"/> on their own, as if
    /// they were the rest of the input (C11 6.10.3.1's argument prescan, and the
    /// tokens of a <c>#if</c> or <c>#line</c>).</summary>
    public List<Item> ExpandList(IReadOnlyList<Item> tokens)
    {
        var result = new List<Item>(tokens.Count);
        if (tokens.Count == 0) { return result; }
        var source = new Source(null);
        source.PushFront(tokens);
        var anchorLine = tokens[0].Position.Line;
        while (source.TryTakePending(out var token))
        {
            Step(token, source, result.Add, anchorLine);
        }
        return result;
    }

    /// <summary>
    /// One rescan step: <paramref name="token"/> is either final (emitted) or a
    /// macro invocation whose replacement is pushed back onto
    /// <paramref name="source"/>.
    /// </summary>
    private void Step(Item token, Source source, Action<Item> emit, int anchorLine)
    {
        if (token.ID != _idSymbol || token is ExpandedItem || token.Content is not string name)
        {
            emit(token);
            return;
        }
        var hide = HideOf(token);
        if (hide.Contains(name))
        {
            emit(token);
            return;
        }
        if (!_cpp.TryGetMacro(name, out var macro))
        {
            // __LINE__ / __FILE__ are not macros in the table; a source token
            // reports its own line, a token from a replacement the line of the
            // outermost invocation it came from.
            emit(_cpp.PredefinedValue(token, hide.IsEmpty ? token.Position.Line : anchorLine) ?? token);
            return;
        }
        if (!macro.IsFunctionLike)
        {
            source.PushFront(Substitute(macro, invocation: null, hide.With(name)));
            return;
        }
        // A function-like macro name is an invocation only when `(` follows.
        if (!source.TryPeek(out var next) || next.ID != _openParenSymbol)
        {
            emit(token);
            return;
        }
        source.TryNext(out _);
        var (args, commas, close) = CollectArgs(source, token);
        CheckArity(macro, args, token);
        source.PushFront(Substitute(macro, new Invocation(args, commas), hide.Intersect(HideOf(close)).With(name)));
    }

    /// <summary>A function-like invocation's arguments and the commas that
    /// separated them (kept for <c>__VA_ARGS__</c>, which rejoins the variable
    /// arguments with their own commas).</summary>
    private sealed record Invocation(List<List<Item>> Args, List<Item> Commas);

    /// <summary>
    /// Collect a function-like invocation's arguments after its <c>(</c>: the
    /// comma-separated token lists at parenthesis depth one, up to the matching
    /// <c>)</c>, which is returned too (its hide set bounds the replacement's).
    /// An argument list is never empty: <c>f()</c> has one empty argument.
    /// </summary>
    private (List<List<Item>> Args, List<Item> Commas, Item Close) CollectArgs(Source source, Item name)
    {
        var args = new List<List<Item>>();
        var commas = new List<Item>();
        var current = new List<Item>();
        var depth = 1;
        while (source.TryNext(out var t))
        {
            if (t.ID == _openParenSymbol) { depth++; }
            else if (t.ID == _closeParenSymbol)
            {
                if (--depth == 0)
                {
                    args.Add(current);
                    return (args, commas, t);
                }
            }
            else if (t.ID == _commaSymbol && depth == 1)
            {
                args.Add(current);
                commas.Add(t);
                current = new List<Item>();
                continue;
            }
            current.Add(t);
        }
        throw new CompileException(
            $"{_cpp.DiagnosticFile}:{name.Position.Line}: error: unterminated argument list invoking macro \"{name.Content}\"");
    }

    /// <summary>The argument count must match the parameter count (at least the
    /// named parameters for a variadic macro); <c>f()</c> of a macro without
    /// parameters passes none.</summary>
    private void CheckArity(MacroDef macro, List<List<Item>> args, Item name)
    {
        var parameters = macro.Params?.Count ?? 0;
        var given = parameters == 0 && args.Count == 1 && args[0].Count == 0 ? 0 : args.Count;
        var where = $"{_cpp.DiagnosticFile}:{name.Position.Line}: error: macro \"{macro.Name}\"";
        if (macro.IsVariadic)
        {
            if (given < parameters)
            {
                throw new CompileException($"{where} requires at least {parameters} arguments, but only {given} given");
            }
            return;
        }
        if (given < parameters)
        {
            throw new CompileException($"{where} requires {parameters} arguments, but only {given} given");
        }
        if (given > parameters)
        {
            throw new CompileException($"{where} passed {given} arguments, but takes just {parameters}");
        }
    }

    /// <summary>
    /// The per-invocation view of the arguments: each parameter's tokens as
    /// written (for <c>#</c> and <c>##</c>) and, computed on first use, fully
    /// expanded (for every other use).
    /// </summary>
    private sealed class Arguments
    {
        private readonly MacroEngine _engine;
        private readonly Dictionary<string, List<Item>> _raw = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Item>> _expanded = new(StringComparer.Ordinal);

        public Arguments(MacroEngine engine, MacroDef macro, Invocation? invocation)
        {
            _engine = engine;
            if (macro.Params is not { } parameters || invocation is null) { return; }
            var args = invocation.Args;
            for (var i = 0; i < parameters.Count; i++)
            {
                _raw[parameters[i]] = i < args.Count ? args[i] : new List<Item>();
            }
            if (!macro.IsVariadic) { return; }
            // The variable arguments, with the commas that separated them.
            var extras = new List<Item>();
            for (var i = parameters.Count; i < args.Count; i++)
            {
                if (i > parameters.Count) { extras.Add(invocation.Commas[i - 1]); }
                extras.AddRange(args[i]);
            }
            _raw[VaArgsName] = extras;
        }

        /// <summary>Whether a variadic invocation passed variable-argument tokens
        /// (<c>__VA_OPT__</c>'s test, on the tokens as written).</summary>
        public bool HasVarArgs => _raw.TryGetValue(VaArgsName, out var va) && va.Count > 0;

        /// <summary>The tokens as written for parameter <paramref name="token"/>,
        /// when it names one.</summary>
        public bool TryRaw(Item token, [NotNullWhen(true)] out List<Item>? raw)
        {
            raw = null;
            return token.ID == _engine._idSymbol && token.Content is string name && _raw.TryGetValue(name, out raw);
        }

        /// <summary>The fully expanded tokens for parameter <paramref name="token"/>,
        /// when it names one.</summary>
        public bool TryExpanded(Item token, [NotNullWhen(true)] out List<Item>? expanded)
        {
            expanded = null;
            if (token.Content is not string name || !TryRaw(token, out var raw)) { return false; }
            if (!_expanded.TryGetValue(name, out expanded))
            {
                expanded = _engine.ExpandList(raw);
                _expanded[name] = expanded;
            }
            return true;
        }
    }

    /// <summary>
    /// The replacement of one invocation: <paramref name="macro"/>'s body with its
    /// parameters substituted and its <c>#</c> / <c>##</c> / <c>__VA_OPT__</c>
    /// operators applied, every token then given <paramref name="hide"/>.
    /// </summary>
    private List<Item> Substitute(MacroDef macro, Invocation? invocation, HideSet hide)
    {
        var arguments = new Arguments(this, macro, invocation);
        var output = new List<Item>(macro.Body.Count);
        SubstituteInto(output, macro.Body, macro, arguments);
        for (var i = 0; i < output.Count; i++)
        {
            var t = output[i];
            var own = HideOf(t);
            var joined = own.Union(hide);
            if (!(t is HiddenItem && ReferenceEquals(joined, own))) { output[i] = new HiddenItem(t, joined); }
        }
        return output;
    }

    /// <summary>
    /// Substitute <paramref name="body"/> (a replacement list, or a
    /// <c>__VA_OPT__</c> group within one) into <paramref name="output"/>. The list
    /// is a sequence of operands joined by <c>##</c>: a parameter next to a
    /// <c>##</c> contributes its tokens as written, any other parameter its
    /// expansion; <c>#</c> turns a parameter into a string literal; and the
    /// operands around each <c>##</c> are glued into one token (C11 6.10.3.2-3).
    /// </summary>
    private void SubstituteInto(List<Item> output, IReadOnlyList<Item> body, MacroDef macro, Arguments arguments)
    {
        List<Item>? chain = null;
        var pasteNext = false;
        for (var i = 0; i < body.Count; i++)
        {
            var bt = body[i];
            if (bt.ID == _hashHashSymbol && chain is not null && i + 1 < body.Count)
            {
                pasteNext = true;
                continue;
            }
            var start = i;
            List<Item> operand;
            if (macro.IsFunctionLike && bt.ID == _hashSymbol && i + 1 < body.Count && arguments.TryRaw(body[i + 1], out var toStringify))
            {
                operand = new List<Item> { Stringize(toStringify, bt.Position) };
                i++;
            }
            else if (macro.IsVariadic && bt.ID == _idSymbol && (bt.Content as string) == VaOptName
                && i + 1 < body.Count && body[i + 1].ID == _openParenSymbol
                && MatchingParen(body, i + 1) is var close && close > 0)
            {
                operand = new List<Item>();
                if (arguments.HasVarArgs)
                {
                    var group = new List<Item>(close - i - 2);
                    for (var j = i + 2; j < close; j++) { group.Add(body[j]); }
                    SubstituteInto(operand, group, macro, arguments);
                }
                i = close;
            }
            else
            {
                var besidePaste = (start > 0 && body[start - 1].ID == _hashHashSymbol)
                    || (i + 1 < body.Count && body[i + 1].ID == _hashHashSymbol);
                if (besidePaste && arguments.TryRaw(bt, out var raw))
                {
                    operand = new List<Item>(raw);
                }
                else if (arguments.TryExpanded(bt, out var expanded))
                {
                    operand = new List<Item>(expanded);
                }
                else
                {
                    operand = new List<Item> { bt };
                }
            }

            if (pasteNext && chain is not null)
            {
                pasteNext = false;
                // GNU `, ## __VA_ARGS__`: with no variable arguments the comma
                // goes; with some, they follow it unpasted.
                if (macro.IsVariadic && (bt.Content as string) == VaArgsName
                    && chain.Count > 0 && chain[^1].ID == _commaSymbol)
                {
                    if (operand.Count == 0) { chain.RemoveAt(chain.Count - 1); }
                    else { chain.AddRange(operand); }
                    continue;
                }
                chain = Glue(chain, operand);
                continue;
            }
            if (chain is not null) { output.AddRange(chain); }
            chain = operand;
        }
        if (chain is not null) { output.AddRange(chain); }
    }

    /// <summary>The index of the <c>)</c> matching the <c>(</c> at
    /// <paramref name="open"/>, or -1.</summary>
    private int MatchingParen(IReadOnlyList<Item> body, int open)
    {
        var depth = 0;
        for (var j = open; j < body.Count; j++)
        {
            if (body[j].ID == _openParenSymbol) { depth++; }
            else if (body[j].ID == _closeParenSymbol && --depth == 0) { return j; }
        }
        return -1;
    }

    /// <summary>
    /// <c>##</c>: glue the last token of <paramref name="left"/> to the first of
    /// <paramref name="right"/> by lexing their joined spelling, which must give
    /// exactly one preprocessing token (C11 6.10.3.3p3). An empty operand (an empty
    /// argument's placemarker) leaves the other side as it is.
    /// </summary>
    private List<Item> Glue(List<Item> left, List<Item> right)
    {
        if (right.Count == 0) { return left; }
        if (left.Count == 0) { return new List<Item>(right); }
        var l = left[^1];
        var r = right[0];
        var ls = Spelling(l);
        var rs = Spelling(r);
        var lexed = _cpp.LexPreprocessingTokens(ls + rs);
        if (lexed.Count != 1)
        {
            throw new CompileException(
                $"{_cpp.DiagnosticFile}:{l.Position.Line}: error: pasting \"{ls}\" and \"{rs}\" does not give a valid preprocessing token");
        }
        var glued = new List<Item>(left.Count + right.Count - 1);
        for (var k = 0; k < left.Count - 1; k++) { glued.Add(left[k]); }
        glued.Add(new Item(lexed[0].ID, lexed[0].Content, l.Position));
        for (var k = 1; k < right.Count; k++) { glued.Add(right[k]); }
        return glued;
    }

    /// <summary>
    /// <c>#</c>: the spelling of <paramref name="tokens"/> as a string literal
    /// (C11 6.10.3.2p2): tokens that were separated by white space in the source
    /// get one space, adjacent ones none, and <c>"</c> and <c>\</c> are escaped.
    /// </summary>
    private Item Stringize(IReadOnlyList<Item> tokens, SourcePosition position)
    {
        var sb = new System.Text.StringBuilder("\"");
        for (var k = 0; k < tokens.Count; k++)
        {
            if (k > 0 && !CPreprocessor.IsAdjacent(tokens[k - 1], tokens[k])) { sb.Append(' '); }
            foreach (var ch in Spelling(tokens[k]))
            {
                if (ch == '\\' || ch == '"') { sb.Append('\\'); }
                sb.Append(ch);
            }
        }
        sb.Append('"');
        return new Item(_stringSymbol, sb.ToString(), position);
    }

    /// <summary>A token's source spelling.</summary>
    private static string Spelling(Item token) => token.Content as string ?? token.Content?.ToString() ?? string.Empty;
}
