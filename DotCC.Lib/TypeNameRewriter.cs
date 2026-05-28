#nullable enable

using System;
using System.Collections.Generic;
using LALR.CC;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>
/// The C "lexer hack" as a <see cref="RewritingTokenStream"/>. Sits between
/// the preprocessor and the parser's <c>SyncLATokenIterator</c>:
/// <list type="bullet">
///   <item>When the upstream emits a <c>typedef</c> directive, the rewriter
///     consumes the body up to the matching top-level <c>;</c>, identifies the
///     new alias name (the last <c>ID</c> token at brace-depth zero), and
///     adds it to the set of known type names. The body tokens themselves
///     are still forwarded so the parser reduces the <c>typedefAlias</c> or
///     <c>typedefStruct</c> action and the visitor gets to emit the C# alias
///     (or struct decl).</item>
///   <item>For any subsequent <c>ID</c> whose content matches a known type
///     name, the rewriter swaps the token's symbol id for <c>TYPE_NAME</c>
///     (content unchanged). The parser then unambiguously reduces
///     <c>Type -> TYPE_NAME</c> and never confuses <c>Color * x;</c>
///     (declaration when <c>Color</c> is a typedef) with a multiplication
///     expression.</item>
/// </list>
/// </summary>
/// <remarks>
/// State is a single <see cref="HashSet{T}"/> of typedef'd names. There's no
/// scope tracking — we don't yet support function-local typedefs, and any
/// shadowing by a local variable of the same name will surface as a
/// downstream C# compile error. Real C disambiguates via the identifier's
/// ordinary-vs-typename namespace, which would need scope-aware lexing —
/// a follow-up.
/// <para>
/// All the iterator plumbing (ready queue, look-ahead buffer, exhaustion
/// flag) lives in <see cref="RewritingTokenStream"/>; this class is pure
/// policy. The pattern shape — track state, optionally consume neighbouring
/// tokens, then emit (possibly transformed) tokens — is the same one
/// <see cref="PreprocessorTokenStream"/> uses for macro expansion.
/// </para>
/// </remarks>
internal sealed class TypeNameRewriter : RewritingTokenStream
{
    private readonly int _idSymbol;
    private readonly int _typeNameSymbol;
    private readonly int _typedefSymbol;
    private readonly int _semiSymbol;
    private readonly int _openBraceSymbol;
    private readonly int _closeBraceSymbol;
    private readonly int _openParenSymbol;
    private readonly int _closeParenSymbol;
    private readonly int _starSymbol;
    private readonly HashSet<string> _typeNames = new(StringComparer.Ordinal);

    public TypeNameRewriter(ISyncIterator<Item> inner) : base(inner)
    {
        // Resolve symbol ids by name from the generated grammar's definition.
        // Done once at construction so per-token dispatch stays O(1).
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sym in C.Definition.SymbolNames)
        {
            map[sym.Name] = sym.ID;
        }
        _idSymbol = map["ID"];
        _typeNameSymbol = map["TYPE_NAME"];
        _typedefSymbol = map["typedef"];
        _semiSymbol = map[";"];
        _openBraceSymbol = map["{"];
        _closeBraceSymbol = map["}"];
        _openParenSymbol = map["("];
        _closeParenSymbol = map[")"];
        _starSymbol = map["*"];
    }

    protected override void ProcessToken(Item token)
    {
        // Promote a known typedef-name ID to TYPE_NAME so the parser routes it
        // through `Type -> TYPE_NAME`. The original ID's content (the
        // identifier string) and source position carry over.
        if (token.ID == _idSymbol
            && token.Content is string name
            && _typeNames.Contains(name))
        {
            Emit(new Item(_typeNameSymbol, name, token.Position));
            return;
        }

        if (token.ID == _typedefSymbol)
        {
            HandleTypedef(token);
            return;
        }

        Emit(token);
    }

    /// <summary>
    /// On <c>typedef</c>: drain tokens up to (and including) the matching
    /// top-level <c>;</c>, capture the alias name, register it, and emit the
    /// whole sequence so the parser still reduces the typedef production.
    /// Brace-tracking ensures <c>typedef struct Foo { … } Foo;</c> stops at
    /// the trailing semicolon, not the one between the members.
    /// </summary>
    private void HandleTypedef(Item typedefToken)
    {
        var body = new List<Item>();
        var depth = 0;
        while (TryReadNext(out var next))
        {
            body.Add(next);
            if (next.ID == _openBraceSymbol)
            {
                depth++;
            }
            else if (next.ID == _closeBraceSymbol)
            {
                depth--;
            }
            else if (next.ID == _semiSymbol && depth == 0)
            {
                break;
            }
        }

        // Identify the alias name: the last ID token at brace-depth zero
        // before the terminating semicolon. Walk the body in reverse,
        // tracking the same depth so we skip member-list identifiers.
        var aliasName = FindAliasName(body);
        if (aliasName is not null)
        {
            _typeNames.Add(aliasName);
        }

        // Emit the typedef token + body so the parser sees the full sequence.
        // Body IDs that already match a known typedef-name get promoted to
        // TYPE_NAME (covers `typedef Color Color2;` chaining); the alias
        // position itself stays as ID because we've identified it from the
        // raw token list, not from the promoted stream.
        Emit(typedefToken);
        var aliasIndex = body.Count - 1;
        // walk backward to find the trailing ID's index (alias position)
        for (var i = body.Count - 1; i >= 0; i--)
        {
            if (body[i].ID == _idSymbol)
            {
                aliasIndex = i;
                break;
            }
        }
        for (var i = 0; i < body.Count; i++)
        {
            var t = body[i];
            if (i != aliasIndex
                && t.ID == _idSymbol
                && t.Content is string s
                && _typeNames.Contains(s)
                && s != aliasName)
            {
                Emit(new Item(_typeNameSymbol, s, t.Position));
            }
            else
            {
                Emit(t);
            }
        }
    }

    private string? FindAliasName(List<Item> body)
    {
        // Function-pointer typedef has the alias INSIDE the first parenthesized
        // group: `typedef Ret (*Name)(args);`. The body has `( * ID )` at
        // brace/paren depth 0 — scan for that pattern first.
        for (var i = 0; i + 3 < body.Count; i++)
        {
            if (body[i].ID == _openParenSymbol
                && body[i + 1].ID == _starSymbol
                && body[i + 2].ID == _idSymbol
                && body[i + 3].ID == _closeParenSymbol)
            {
                return body[i + 2].Content as string;
            }
        }

        // Otherwise (simple alias / struct-with-tag / struct-with-body):
        // reverse-walk for the last top-level ID. Skip anything inside braces
        // OR parens — parens guard the param list of a fnptr typedef (handled
        // above) and any nested expressions that aren't the alias position.
        var depth = 0;
        for (var i = body.Count - 1; i >= 0; i--)
        {
            var t = body[i];
            if (t.ID == _closeBraceSymbol || t.ID == _closeParenSymbol) { depth++; continue; }
            if (t.ID == _openBraceSymbol  || t.ID == _openParenSymbol)  { depth--; continue; }
            if (depth != 0) { continue; }
            if (t.ID == _idSymbol && t.Content is string s)
            {
                return s;
            }
        }
        return null;
    }

    public override void Reset()
    {
        _typeNames.Clear();
        base.Reset();
    }
}
