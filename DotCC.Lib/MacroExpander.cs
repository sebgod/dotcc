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
    /// the actual arg-token lists. Missing args (fewer actuals than formals)
    /// expand to nothing — matches real C's "empty argument" behavior.
    /// Extra args (more actuals than formals) are dropped; v1 doesn't yet
    /// support C99 variadic <c>__VA_ARGS__</c>.
    /// </summary>
    private List<Item> Substitute(MacroDef macro, List<List<Item>> args)
    {
        var paramMap = new Dictionary<string, IReadOnlyList<Item>>(StringComparer.Ordinal);
        for (var i = 0; i < macro.Params!.Count; i++)
        {
            paramMap[macro.Params[i]] = i < args.Count ? args[i] : Array.Empty<Item>();
        }

        var result = new List<Item>(macro.Body.Count);
        foreach (var bt in macro.Body)
        {
            if (bt.ID == _idSymbol
                && bt.Content is string pname
                && paramMap.TryGetValue(pname, out var argTokens))
            {
                result.AddRange(argTokens);
            }
            else
            {
                result.Add(bt);
            }
        }
        return result;
    }
}
