#nullable enable

using System;
using System.Collections.Generic;
using LALR.CC;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>
/// Strips the C type qualifier <c>const</c> from the token stream as a
/// <see cref="RewritingTokenStream"/>. dotcc has no C# representation for it —
/// <c>const char *p</c> lowers exactly like <c>char *p</c> (see the <c>TsConst</c>
/// note in <see cref="CSharpEmitter"/>) — so rather than thread <c>const</c>
/// productions through every type position in the grammar, we delete the token
/// before the parser ever sees it. (Const-correctness — diagnosing writes through
/// a <c>const</c> lvalue — is a wanted future feature that would instead preserve
/// the qualifier into a CType layer.)
/// </summary>
/// <remarks>
/// Deleting <c>const</c> unblocks <c>const &lt;typedef-name&gt;</c> / <c>const
/// struct X</c> / east-const (<c>T const</c>) / multi-qualifier runs, none of
/// which the grammar could accept: a qualifier preceding a <c>TYPE_NAME</c> (or a
/// <c>struct</c>/<c>union</c>/<c>enum</c> tag) has no production, and adding one
/// creates an unavoidable LALR reduce/reduce conflict (at the first <c>const</c>
/// the parser cannot tell whether a built-in specifier list or a typedef-name base
/// follows). Deletion sidesteps the ambiguity while preserving drop-the-qualifier
/// semantics, so the emitted C# is unchanged.
/// <para>
/// <c>volatile</c> is NO LONGER stripped here. It now parses as a leading Type
/// prefix (<c>Type → 'volatile' Type</c>) so the emitter can lower reads/writes of
/// a volatile lvalue to <c>Volatile.Read</c>/<c>Volatile.Write</c> — faithful
/// rather than erased. (East/mid <c>int volatile</c> is consequently no longer
/// accepted; leading <c>volatile T</c> is the universal form.) <c>restrict</c> is
/// likewise left alone — it only qualifies a pointer and is handled by the
/// <c>TypePtrQualRestrict</c> production.
/// </para>
/// <para>
/// Safety: <c>const</c> is ONLY a type qualifier in C — it has no other syntactic
/// role — so removing it can never turn a valid parse invalid (the neighbouring
/// tokens were already legal with the qualifier between them).
/// </para>
/// <para>
/// Placement is AFTER <see cref="MacroExpander"/> (so a macro expanding to
/// <c>const</c> is normalised too) and BEFORE <see cref="TypeNameRewriter"/> (so
/// a <c>typedef const int Foo;</c> registers <c>Foo</c> from an already-stripped
/// <c>typedef int Foo;</c> stream). Like <see cref="DialectKeywordRewriter"/>,
/// it is not run on the <c>-E</c> preprocess-only path: qualifier removal is a
/// parse concern, and <c>clang -E</c> leaves <c>const</c> in place, so we do too.
/// </para>
/// </remarks>
internal sealed class QualifierStripper : RewritingTokenStream
{
    private readonly int _constSymbol;

    public QualifierStripper(ISyncIterator<Item> inner) : base(inner)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sym in C.Definition.SymbolNames)
        {
            map[sym.Name] = sym.ID;
        }
        _constSymbol = map["const"];
    }

    protected override void ProcessToken(Item token)
    {
        // Drop `const` — emit nothing, and MoveNext pulls the next token.
        // `volatile` is passed through (it parses as a Type prefix now).
        if (token.ID == _constSymbol) { return; }
        Emit(token);
    }
}
