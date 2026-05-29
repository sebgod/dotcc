#nullable enable

using System;
using System.Collections.Generic;
using LALR.CC;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>
/// Dialect-aware keyword promotion as a <see cref="RewritingTokenStream"/>.
/// Sits between <see cref="MacroExpander"/> and <see cref="TypeNameRewriter"/>
/// and promotes selected <c>ID</c> tokens to a keyword terminal — but only
/// when the active <see cref="CDialect"/> is new enough that the spelling is
/// a reserved keyword rather than an ordinary identifier.
/// </summary>
/// <remarks>
/// This is the "rule 2" layer of the dialect-gating model (see the C23
/// handover): new keywords spelled like identifiers (<c>bool</c>,
/// <c>true</c>, <c>false</c>, <c>nullptr</c>, …) cannot be gated in the
/// visitor, because the parser runs first and <c>int true = 5;</c> is valid
/// pre-C23 code. If the grammar always treated them as keywords, valid older
/// code would hit a parse error before semantic analysis ever ran. So the
/// <c>ID → keyword</c> promotion itself carries a minimum dialect version.
/// <para>
/// Placement <em>after</em> <see cref="MacroExpander"/> is deliberate: when
/// the user <c>#include</c>s a header that <c>#define</c>s the spelling (e.g.
/// pre-C23 <c>&lt;stdbool.h&gt;</c>'s <c>#define bool _Bool</c>), the macro has
/// already expanded by the time tokens reach here, so the keyword form arrives
/// directly and the promotion table simply doesn't fire. The table only kicks
/// in for the bare, un-<c>#include</c>d keyword under a dialect that makes it
/// first-class — exactly the C23 case.
/// </para>
/// <para>
/// The <c>-E</c> preprocess-only path deliberately does NOT run this rewriter:
/// keyword status is not a preprocessor concept, so <c>clang -E</c> leaves
/// <c>bool</c> as <c>bool</c>, and so do we.
/// </para>
/// </remarks>
internal sealed class DialectKeywordRewriter : RewritingTokenStream
{
    private readonly int _idSymbol;
    private readonly int _version;

    /// <summary>
    /// Promotion table, keyed by identifier spelling. <c>MinVersion</c> is the
    /// first C dialect (89/99/11/17/23) in which the spelling is a keyword;
    /// <c>TargetSymbol</c> / <c>TargetText</c> are the terminal the parser
    /// should see. Data, not code — append a row per new keyword as the C23
    /// (and later C2y) surface lands. The target terminal must already exist
    /// in the grammar for the row to be useful.
    /// </summary>
    private readonly Dictionary<string, (int MinVersion, int TargetSymbol, string TargetText)> _promotions;

    public DialectKeywordRewriter(ISyncIterator<Item> inner, CDialect dialect) : base(inner)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sym in C.Definition.SymbolNames)
        {
            map[sym.Name] = sym.ID;
        }
        _idSymbol = map["ID"];
        _version = dialect.Version;

        _promotions = new Dictionary<string, (int, int, string)>(StringComparer.Ordinal)
        {
            // C23 makes `bool` a first-class keyword — no <stdbool.h> needed.
            // The grammar already has `_Bool` (TypeSpec -> _Bool, action
            // tsBool), so we promote the C23 spelling onto that existing
            // terminal. Pre-C23, `bool` stays an ordinary ID: the MinVersion
            // gate keeps valid old code that uses `bool` as an identifier
            // parsing, AND lets <stdbool.h>'s `#define bool _Bool` macro do
            // the job when the header is included. One gate, both directions.
            ["bool"] = (23, map["_Bool"], "_Bool"),

            // C23 predefined constants — first-class keywords with no header.
            // Pre-C23 they are <stdbool.h>/<stddef.h> macros (or plain
            // identifiers), so the MinVersion gate keeps old code parsing and
            // lets the macro path win when a header is included. Each promotes
            // onto a dedicated grammar terminal (TRUE/FALSE/NULLPTR) that has
            // no lexer rule, so this rewriter is the only way they appear.
            ["true"]    = (23, map["TRUE"],    "true"),
            ["false"]   = (23, map["FALSE"],   "false"),
            ["nullptr"] = (23, map["NULLPTR"], "nullptr"),

            // C23 makes `static_assert` a first-class keyword (the C11 form is
            // `_Static_assert`, with the lowercase macro living in <assert.h>).
            // Promote onto the existing `_Static_assert` grammar terminal under
            // c23; pre-C23 it stays an ID and the <assert.h> macro path (if any)
            // applies. The `_Static_assert` spelling itself is always a keyword
            // (lexer rule), needing no promotion.
            ["static_assert"] = (23, map["_Static_assert"], "_Static_assert"),
        };
    }

    protected override void ProcessToken(Item token)
    {
        if (token.ID == _idSymbol
            && token.Content is string name
            && _promotions.TryGetValue(name, out var p)
            && _version >= p.MinVersion)
        {
            // Emit the keyword terminal with its canonical spelling so the
            // token is indistinguishable from a lexer-produced keyword (the
            // visitor for these actions ignores content, but keeping the
            // spelling honest avoids surprises in any future content-aware
            // handling).
            Emit(new Item(p.TargetSymbol, p.TargetText, token.Position));
            return;
        }

        Emit(token);
    }
}
