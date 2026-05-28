#nullable enable

using System;
using System.Collections.Generic;
using LALR.CC;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>
/// Function-like macro expander, sitting between <see cref="PreprocessorTokenStream"/>
/// and <see cref="TypeNameRewriter"/> in the pipeline. For each <c>ID</c>
/// token whose content names a function-like macro in <see cref="CPreprocessor"/>'s
/// macro table, we peek for an immediately-following <c>(</c>; if present,
/// collect comma-separated args (paren-balanced) and substitute each formal
/// parameter in the body with the corresponding actual-argument token list.
/// </summary>
/// <remarks>
/// Object-like macros are handled by <see cref="CPreprocessor.Rewrite"/> in
/// the upstream <see cref="PreprocessorTokenStream"/>, which doesn't need
/// lookahead — by the time we see the token here, the substitution has
/// already happened. Function-like calls reach us with the macro name intact
/// because <c>Rewrite</c> skips them.
/// <para>
/// Multi-pass rescan via <see cref="ExpandTokenList"/>: after substituting
/// the body, we walk the result and re-expand any nested macro calls
/// (function-like) or names (object-like). A per-call hiding set carries
/// names currently being expanded to prevent self-recursion — matches the
/// classical C standard "macro replacement list rescan" rule
/// (<c>#define X X</c> stops at one level; <c>X</c> stays as <c>X</c>).
/// </para>
/// </remarks>
internal sealed class MacroExpander : RewritingTokenStream
{
    private readonly CPreprocessor _cpp;
    private readonly int _idSymbol;
    private readonly int _openParenSymbol;
    private readonly int _closeParenSymbol;
    private readonly int _commaSymbol;
    private readonly int _stringSymbol;
    private readonly int _hashSymbol;
    private readonly int _hashHashSymbol;
    private const string VaArgsName = "__VA_ARGS__";

    public MacroExpander(ISyncIterator<Item> inner, CPreprocessor cpp) : base(inner)
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

    protected override void ProcessToken(Item token)
    {
        if (token.ID == _idSymbol
            && token.Content is string name
            && _cpp.TryGetMacro(name, out var macro)
            && macro.IsFunctionLike)
        {
            // Peek for `(` — that's what makes this a call rather than a
            // bare reference. If the next token is anything else, hold it
            // back and emit the macro name verbatim (real C: a function-like
            // macro name without `(` is just an identifier — sometimes used
            // intentionally to take a "function pointer" through #if-tested
            // alternatives).
            if (TryReadNext(out var next))
            {
                if (next.ID == _openParenSymbol)
                {
                    var args = CollectArgsFromStream();
                    var hiding = new HashSet<string>(StringComparer.Ordinal) { name };
                    var substituted = Substitute(macro, args);
                    EmitRange(ExpandTokenList(substituted, hiding));
                    return;
                }
                HoldNext(next);
            }
            Emit(token);
            return;
        }

        Emit(token);
    }

    /// <summary>
    /// Walk <paramref name="tokens"/>, expanding any macro calls (function-
    /// like via following <c>(args)</c>) or names (object-like) we find.
    /// <paramref name="hiding"/> carries the names currently mid-expansion;
    /// each recursive expansion adds its own name to prevent infinite
    /// self-recursion (the classical rescan rule).
    /// </summary>
    private List<Item> ExpandTokenList(IReadOnlyList<Item> tokens, HashSet<string> hiding)
    {
        var result = new List<Item>();
        var i = 0;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.ID == _idSymbol
                && t.Content is string name
                && _cpp.TryGetMacro(name, out var macro)
                && !hiding.Contains(name))
            {
                if (macro.IsFunctionLike)
                {
                    // Need `(` immediately after to be a call. The body list
                    // is self-contained — no upstream peek required.
                    if (i + 1 < tokens.Count && tokens[i + 1].ID == _openParenSymbol)
                    {
                        var (args, endIdx) = CollectArgsFromList(tokens, i + 2);
                        if (endIdx >= 0)
                        {
                            hiding.Add(name);
                            var substituted = Substitute(macro, args);
                            result.AddRange(ExpandTokenList(substituted, hiding));
                            hiding.Remove(name);
                            i = endIdx + 1;
                            continue;
                        }
                    }
                    // function-like name with no `(` — emit as identifier.
                }
                else
                {
                    // Object-like: recursively expand the body (so chains
                    // like `#define A B`, `#define B C` collapse to C).
                    hiding.Add(name);
                    result.AddRange(ExpandTokenList(macro.Body, hiding));
                    hiding.Remove(name);
                    i++;
                    continue;
                }
            }
            result.Add(t);
            i++;
        }
        return result;
    }

    /// <summary>
    /// Drain tokens from the upstream until the matching close-paren at
    /// depth 0. The opening <c>(</c> has already been consumed by the
    /// caller. Returns a list-of-lists: one inner list per comma-separated
    /// argument. Nested parens (e.g. <c>MAX(MIN(a,b), c)</c>) count toward
    /// depth so we don't split args on commas inside them.
    /// </summary>
    private List<List<Item>> CollectArgsFromStream()
    {
        var args = new List<List<Item>>();
        var current = new List<Item>();
        var depth = 1;
        var sawAny = false;
        while (TryReadNext(out var t))
        {
            sawAny = true;
            if (t.ID == _openParenSymbol)
            {
                depth++;
                current.Add(t);
                continue;
            }
            if (t.ID == _closeParenSymbol)
            {
                depth--;
                if (depth == 0)
                {
                    if (current.Count > 0 || args.Count > 0) { args.Add(current); }
                    return args;
                }
                current.Add(t);
                continue;
            }
            if (t.ID == _commaSymbol && depth == 1)
            {
                args.Add(current);
                current = new List<Item>();
                continue;
            }
            current.Add(t);
        }
        // Unbalanced — input ended before matching ')'. Emit what we have.
        if (sawAny && (current.Count > 0 || args.Count > 0)) { args.Add(current); }
        return args;
    }

    /// <summary>
    /// Variant of <see cref="CollectArgsFromStream"/> that reads from an
    /// in-memory token list (used by the rescan walker). The starting
    /// index <paramref name="start"/> points at the first token AFTER the
    /// opening <c>(</c>. Returns the args plus the index of the matching
    /// close-paren (or -1 if unbalanced).
    /// </summary>
    private (List<List<Item>> args, int endIdx) CollectArgsFromList(IReadOnlyList<Item> tokens, int start)
    {
        var args = new List<List<Item>>();
        var current = new List<Item>();
        var depth = 1;
        for (var i = start; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.ID == _openParenSymbol)
            {
                depth++;
                current.Add(t);
                continue;
            }
            if (t.ID == _closeParenSymbol)
            {
                depth--;
                if (depth == 0)
                {
                    if (current.Count > 0 || args.Count > 0) { args.Add(current); }
                    return (args, i);
                }
                current.Add(t);
                continue;
            }
            if (t.ID == _commaSymbol && depth == 1)
            {
                args.Add(current);
                current = new List<Item>();
                continue;
            }
            current.Add(t);
        }
        return (args, -1);
    }

    /// <summary>
    /// Substitute formal parameters in <paramref name="macro"/>'s body with
    /// the actual arg-token lists. Handles three operators inline:
    /// <list type="bullet">
    ///   <item><c>#PARAM</c> stringification: reconstructs the arg's source
    ///     text into a STRING token (escapes <c>"</c> and <c>\</c>).</item>
    ///   <item><c>LHS ## RHS</c> token-pasting: glues the last token of
    ///     LHS with the first token of RHS into a single ID-shaped token
    ///     carrying the concatenated text. Either side may be a formal
    ///     param (substituted first) or a literal token.</item>
    ///   <item><c>__VA_ARGS__</c>: bound to all extra invocation args
    ///     beyond the formal count, comma-joined, when the macro is
    ///     declared variadic (<c>#define LOG(fmt, ...)</c>).</item>
    /// </list>
    /// Missing args expand to nothing; extras beyond the formal count are
    /// dropped (non-variadic) or accumulated into <c>__VA_ARGS__</c>
    /// (variadic).
    /// </summary>
    private List<Item> Substitute(MacroDef macro, List<List<Item>> args)
    {
        var paramMap = new Dictionary<string, IReadOnlyList<Item>>(StringComparer.Ordinal);
        for (var i = 0; i < macro.Params!.Count; i++)
        {
            paramMap[macro.Params[i]] = i < args.Count ? args[i] : Array.Empty<Item>();
        }
        if (macro.IsVariadic)
        {
            // Extras beyond the named params land in __VA_ARGS__,
            // comma-joined (real C: the args' separating commas are
            // preserved verbatim, including spaces around them).
            var extras = new List<Item>();
            var startIdx = macro.Params.Count;
            for (var i = startIdx; i < args.Count; i++)
            {
                if (i > startIdx)
                {
                    extras.Add(new Item(_commaSymbol, ",", default));
                }
                extras.AddRange(args[i]);
            }
            paramMap[VaArgsName] = extras;
        }

        var body = macro.Body;
        var result = new List<Item>(body.Count);
        for (var i = 0; i < body.Count; i++)
        {
            var bt = body[i];

            // `LHS ## RHS` — token-paste. Lookahead at i+1 for `##` and
            // i+2 for RHS. Either side might be a formal param or a
            // literal token. Substitute first, then paste the last token
            // of LHS with the first token of RHS.
            if (i + 2 < body.Count && body[i + 1].ID == _hashHashSymbol)
            {
                var lhs = ResolveOperand(bt, paramMap);
                var rhs = ResolveOperand(body[i + 2], paramMap);
                AppendPasted(result, lhs, rhs, bt.Position);
                i += 2;
                continue;
            }

            // `# PARAM` — stringify. Only meaningful when followed by a
            // formal param name; if not, emit `#` as-is (which will likely
            // cause a downstream parse error — same as real C).
            if (bt.ID == _hashSymbol
                && i + 1 < body.Count
                && body[i + 1].Content is string pname1
                && paramMap.TryGetValue(pname1, out var stringifyTokens))
            {
                var text = Stringify(stringifyTokens);
                result.Add(new Item(_stringSymbol, "\"" + text + "\"", bt.Position));
                i++;
                continue;
            }

            // Regular param substitution (also covers __VA_ARGS__ since
            // we bound it in paramMap above).
            if (bt.ID == _idSymbol
                && bt.Content is string pname2
                && paramMap.TryGetValue(pname2, out var argTokens))
            {
                result.AddRange(argTokens);
                continue;
            }

            result.Add(bt);
        }
        return result;
    }

    /// <summary>
    /// Resolve a paste-operand. Formal-param names look up the
    /// corresponding actual-arg token list; anything else passes through
    /// as a single-token list.
    /// </summary>
    private IReadOnlyList<Item> ResolveOperand(Item t, Dictionary<string, IReadOnlyList<Item>> paramMap)
    {
        if (t.ID == _idSymbol
            && t.Content is string s
            && paramMap.TryGetValue(s, out var tokens))
        {
            return tokens;
        }
        return new[] { t };
    }

    /// <summary>
    /// Emit <paramref name="lhs"/> + <paramref name="rhs"/> with the last
    /// token of lhs glued to the first token of rhs into one ID-shaped
    /// token. When either side is empty, the other side passes through
    /// untouched (matches the C rule "pasting an empty operand is a no-op").
    /// </summary>
    private void AppendPasted(List<Item> result, IReadOnlyList<Item> lhs, IReadOnlyList<Item> rhs, SourcePosition position)
    {
        if (lhs.Count == 0)
        {
            result.AddRange(rhs);
            return;
        }
        if (rhs.Count == 0)
        {
            result.AddRange(lhs);
            return;
        }
        for (var k = 0; k < lhs.Count - 1; k++) { result.Add(lhs[k]); }
        var pasted = (lhs[^1].Content?.ToString() ?? string.Empty)
                   + (rhs[0].Content?.ToString() ?? string.Empty);
        result.Add(new Item(_idSymbol, pasted, position));
        for (var k = 1; k < rhs.Count; k++) { result.Add(rhs[k]); }
    }

    /// <summary>
    /// Reconstruct an arg's source text from its token list — used by the
    /// <c>#</c> stringification operator. Tokens are joined with single
    /// spaces (matches what most C preprocessors emit); <c>"</c> and <c>\</c>
    /// inside any token content are backslash-escaped so the resulting
    /// STRING token is well-formed.
    /// </summary>
    private static string Stringify(IReadOnlyList<Item> tokens)
    {
        var sb = new System.Text.StringBuilder();
        var first = true;
        foreach (var t in tokens)
        {
            if (!first) { sb.Append(' '); }
            first = false;
            var c = t.Content?.ToString();
            if (c is null) { continue; }
            foreach (var ch in c)
            {
                if (ch == '\\' || ch == '"') { sb.Append('\\'); }
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }
}
