#nullable enable

using System;
using System.Collections.Generic;
using LALR.CC;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>
/// Strips the C type qualifiers <c>const</c> and <c>volatile</c> from the token
/// stream as a <see cref="RewritingTokenStream"/>. dotcc has no C# representation
/// for either — <c>const char *p</c> lowers exactly like <c>char *p</c> (see the
/// <c>TsConst</c> note in <see cref="CSharpEmitter"/>) — so rather than thread
/// qualifier productions through every type position in the grammar, we delete
/// the tokens before the parser ever sees them.
/// </summary>
/// <remarks>
/// This generalises what the grammar already did piecemeal — <c>TsConst</c> /
/// <c>TsVolatile</c> inside a <c>TypeSpecList</c>, and <c>TypePtrQualConst</c> /
/// <c>TypePtrQualVolatile</c> after a <c>*</c> — to EVERY position, and crucially
/// unblocks <c>const &lt;typedef-name&gt;</c> / <c>const struct X</c> / east-const
/// (<c>T const</c>) / multi-qualifier runs, none of which the grammar could
/// accept. A qualifier preceding a <c>TYPE_NAME</c> (or a <c>struct</c>/
/// <c>union</c>/<c>enum</c> tag) has no production, and adding one creates an
/// unavoidable LALR reduce/reduce conflict for runs like <c>const volatile T</c>:
/// at the first <c>const</c> the parser cannot tell whether a built-in specifier
/// list (<c>const volatile int</c>) or a typedef-name base (<c>const volatile
/// MyT</c>) follows. Deleting the tokens sidesteps the ambiguity entirely while
/// preserving the existing drop-the-qualifier semantics, so the emitted C# is
/// unchanged.
/// <para>
/// Safety: <c>const</c> and <c>volatile</c> are ONLY type qualifiers in C — they
/// have no other syntactic role — so removing one can never turn a valid parse
/// invalid (the neighbouring tokens were already legal with the qualifier between
/// them). <c>restrict</c> is intentionally left alone: it only ever qualifies a
/// pointer and is already handled by the <c>TypePtrQualRestrict</c> production.
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
    private readonly int _volatileSymbol;

    public QualifierStripper(ISyncIterator<Item> inner) : base(inner)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sym in C.Definition.SymbolNames)
        {
            map[sym.Name] = sym.ID;
        }
        _constSymbol = map["const"];
        _volatileSymbol = map["volatile"];
    }

    protected override void ProcessToken(Item token)
    {
        // Drop the qualifier — emit nothing, and MoveNext pulls the next token.
        if (token.ID == _constSymbol || token.ID == _volatileSymbol) { return; }
        Emit(token);
    }
}
