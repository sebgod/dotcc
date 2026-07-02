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
    /// ISO year (<see cref="CDialect.Version"/>: 1990 / 1999 / 2011 / 2017 /
    /// 2023) of the first C standard in which the spelling is a keyword;
    /// <c>TargetSymbol</c> / <c>TargetText</c> are the terminal the parser
    /// should see. <c>Version</c> is keyed by year, so the gate is a plain
    /// monotonic <c>Version &gt;= MinVersion</c>. Data, not code — append a row
    /// per new keyword. The target terminal must already exist in the grammar.
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
            ["bool"] = (2023, map["_Bool"], "_Bool"),

            // C23 predefined constants — first-class keywords with no header.
            // Pre-C23 they are <stdbool.h>/<stddef.h> macros (or plain
            // identifiers), so the MinVersion gate keeps old code parsing and
            // lets the macro path win when a header is included. Each promotes
            // onto a dedicated grammar terminal (TRUE/FALSE/NULLPTR) that has
            // no lexer rule, so this rewriter is the only way they appear.
            ["true"]    = (2023, map["TRUE"],    "true"),
            ["false"]   = (2023, map["FALSE"],   "false"),
            ["nullptr"] = (2023, map["NULLPTR"], "nullptr"),

            // C99 makes `inline` a function specifier. Pre-C99 it's an ordinary
            // identifier (`int inline;` is valid C89), so promote onto the
            // `inline` grammar terminal (no lexer rule) only from the C99 era on
            // — covering c99/c11/c17/c23, the default c17 included. The FnSig
            // path turns the flagged type into a [MethodImpl(AggressiveInlining)].
            ["inline"] = (1999, map["inline"], "inline"),

            // C23 makes `static_assert` a first-class keyword (the C11 form is
            // `_Static_assert`, with the lowercase macro living in <assert.h>).
            // Promote onto the existing `_Static_assert` grammar terminal under
            // c23; pre-C23 it stays an ID and the <assert.h> macro path (if any)
            // applies. The `_Static_assert` spelling itself is always a keyword
            // (lexer rule), needing no promotion.
            ["static_assert"] = (2023, map["_Static_assert"], "_Static_assert"),

            // C23 makes `noreturn` a first-class keyword (the C11 form is
            // `_Noreturn`; the lowercase macro lived in <stdnoreturn.h>). Promote
            // onto the existing `_Noreturn` terminal under c23; pre-C23 it stays
            // an ID. The `_Noreturn` spelling itself is always a keyword.
            ["noreturn"] = (2023, map["_Noreturn"], "_Noreturn"),

            // C23 `typeof` / `typeof_unqual` — yield a type. Both promote onto the
            // `typeof` terminal (dotcc drops qualifiers, so typeof_unqual behaves
            // identically). Pre-C23 they stay ordinary identifiers.
            ["typeof"]        = (2023, map["typeof"], "typeof"),
            ["typeof_unqual"] = (2023, map["typeof"], "typeof"),

            // C23 makes `alignof` / `alignas` first-class keywords (the C11 forms
            // are `_Alignof` / `_Alignas`, with the lowercase macros living in
            // <stdalign.h>). Promote onto the existing terminals under c23;
            // pre-C23 they stay IDs and the <stdalign.h> macro path applies.
            ["alignof"] = (2023, map["_Alignof"], "_Alignof"),
            ["alignas"] = (2023, map["_Alignas"], "_Alignas"),
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
