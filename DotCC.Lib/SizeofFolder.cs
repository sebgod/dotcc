#nullable enable

using System;
using System.Collections.Generic;
using LALR.CC;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>
/// Folds <c>sizeof(T)</c> to its numeric value in the token stream so that
/// <c>sizeof(int) * CHAR_BIT</c> becomes <c>4 * 8</c> which the LALR parser
/// handles without conflict. Without this, the grammar's subscript production
/// (<c>E[E]</c>) creates a conflict that drops binary operators after
/// <c>sizeof</c>, causing e.g. <c>MAXABITS+1=32</c> to become just <c>4</c>.
///
/// A <c>sizeof(struct/union)</c> can't be folded to a number here (the layout
/// — size + alignment padding — isn't known at lex time), so it stays as the
/// <c>sizeof(T)</c> token form. That re-exposes the conflict, but only against
/// the operators that double as unary-prefix operators: <c>sizeof(S) * x</c>,
/// <c>sizeof(S) - x</c>, <c>sizeof(S) &amp; x</c> get parsed as a cast inside
/// the operand (<c>sizeof((S)*x)</c> = the deref's type, dropping <c>* x</c>).
/// For those, the folder wraps the unfoldable sizeof in parens
/// (<c>(sizeof(S)) * x</c>) so it's a complete primary and the operator binds
/// as binary — the struct analogue of the NUM fold. (Other operators —
/// <c>+ | &lt;&lt;</c> … — have no unary form, so they never absorb and need no
/// wrap; and a sizeof NOT followed by one of these — e.g.
/// <c>malloc(sizeof(S))</c> — is left bare so the malloc-sizeof peephole still
/// sees a sole <c>SizeofType</c> marker.)
/// </summary>
internal sealed class SizeofFolder : RewritingTokenStream
{
    private static readonly HashSet<string> TypeKeywords = new(StringComparer.Ordinal)
    { "void", "char", "short", "int", "long", "float", "double", "signed", "unsigned", "_Bool" };

    /// <summary>Cached byte-sizes of typedef names seen so far. Populated as
    /// <c>typedef</c> declarations stream past; consulted during <see cref="EvalSizeof"/>
    /// when a TYPE_NAME is encountered so the folder can still fold <c>sizeof(myint)</c>
    /// to a NUM and avoid the parser's binary-op-after-sizeof conflict.</summary>
    private readonly Dictionary<string, int> _typedefSizes = new(StringComparer.Ordinal);

    private readonly int _sizeofSym, _lparenSym, _rparenSym, _starSym, _numSym, _idSym, _typeNameSym;
    private readonly int _structSym, _unionSym, _enumSym, _typedefSym, _semiSym;
    private readonly int _minusSym, _ampSym;

    public SizeofFolder(ISyncIterator<Item> inner) : base(inner)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sym in C.Definition.SymbolNames)
            map[sym.Name] = sym.ID;
        _sizeofSym = map["sizeof"];
        _lparenSym = map["("];
        _rparenSym = map[")"];
        _starSym = map["*"];
        _minusSym = map["-"];
        _ampSym = map["&"];
        _numSym  = map["NUM"];
        _idSym   = map["ID"];
        _typeNameSym = map["TYPE_NAME"];
        _structSym = map.TryGetValue("struct", out var s) ? s : -1;
        _unionSym  = map.TryGetValue("union", out var u) ? u : -1;
        _enumSym   = map.TryGetValue("enum", out var e) ? e : -1;
        _typedefSym = map.TryGetValue("typedef", out var td) ? td : -1;
        _semiSym = map.TryGetValue(";", out var semi) ? semi : -1;
    }

    protected override void ProcessToken(Item token)
    {
        // Track simple typedefs: `typedef <type> <name> ;`
        // Record the name→size so `sizeof(name)` can fold to a NUM.
        if (_typedefSym >= 0 && token.ID == _typedefSym)
        {
            Emit(token);
            // Collect all tokens to the next `;`
            var declTokens = new List<Item>();
            while (TryReadNext(out var dt) && dt!.ID != _semiSym)
                declTokens.Add(dt);
            if (declTokens.Count >= 2)
            {
                // The new typedef name is the last non-trivial token before `;`
                // (skipping trailing `)` from function-pointer declarators and
                // `*` which can't be part of a typename). Simple heuristic:
                // scan backward for the last ID/TYPE_NAME.
                for (var i = declTokens.Count - 1; i >= 0; i--)
                {
                    if (declTokens[i].ID == _idSym || declTokens[i].ID == _typeNameSym)
                    {
                        var name = declTokens[i].Content?.ToString() ?? "";
                        // Compute size from the type-specifier tokens before the name.
                        var specTokens = declTokens.Take(i).ToList();
                        var sz = EvalSizeof(specTokens);
                        if (sz is int s) { _typedefSizes[name] = s; }
                        break;
                    }
                }
            }
            foreach (var dt in declTokens) Emit(dt);
            // Emit the `;` that ended the loop above (TryReadNext consumed it).
            // Actually TryReadNext consumed the token — we need to emit it manually.
            Emit(new Item(_semiSym, ";", token.Position));
            return;
        }

        if (token.ID != _sizeofSym) { Emit(token); return; }

        // Peek sizeof ( Type )
        if (!TryReadNext(out var lp) || lp!.ID != _lparenSym) { Emit(token); if (lp != null) Emit(lp); return; }

        // Collect type tokens with paren-depth tracking to find the matching )
        var typeTokens = new List<Item>();
        var depth = 1;
        while (depth > 0 && TryReadNext(out var t))
        {
            if (t!.ID == _lparenSym) depth++;
            else if (t.ID == _rparenSym) { depth--; if (depth == 0) break; }
            typeTokens.Add(t);
        }

        if (depth != 0) { Emit(token); Emit(lp); foreach (var x in typeTokens) Emit(x); return; }

        var size = EvalSizeof(typeTokens);
        if (size is int n)
        {
            // Replace entire sizeof(type) sequence with NUM
            Emit(new Item(_numSym, n.ToString(
                System.Globalization.CultureInfo.InvariantCulture), token.Position));
            return;
        }

        // Can't fold (struct/union/enum, or an aggregate typedef) — re-emit the
        // `sizeof ( Type )` tokens. Peek the token that follows: if it's an
        // operator that ALSO has a unary-prefix form (`*` deref, `-` negate, `&`
        // address-of), the parser would absorb it into a cast inside the operand
        // (`sizeof(S) * x` → `sizeof((S)*x)`, silently dropping `* x`). Wrap the
        // sizeof in parens so it's a complete primary and the operator binds as a
        // binary operator. A bare sizeof (anything else following — `)`, `;`, `+`,
        // `|`, …) is left untouched so the malloc-sizeof peephole still sees it.
        var hasNext = TryReadNext(out var nextTok);
        var wrap = hasNext && (nextTok!.ID == _starSym || nextTok.ID == _minusSym
            || nextTok.ID == _ampSym);
        if (wrap) { Emit(new Item(_lparenSym, "(", token.Position)); }
        Emit(token); Emit(lp);
        foreach (var x in typeTokens) Emit(x);
        Emit(new Item(_rparenSym, ")", token.Position));
        if (wrap) { Emit(new Item(_rparenSym, ")", token.Position)); }
        // Re-emit the peeked token (the operator, or whatever followed). It can't
        // be a `sizeof`/`typedef` needing ProcessToken — neither is valid right
        // after a `sizeof(Type)` without an intervening operator — so emitting it
        // directly is safe.
        if (hasNext) { Emit(nextTok!); }
    }

    private int? EvalSizeof(List<Item> tokens)
    {
        if (tokens.Count == 0) return null;
        // Count trailing * (pointers)
        var starCount = 0;
        var end = tokens.Count;
        while (end > 0 && tokens[end - 1].ID == _starSym) { starCount++; end--; }
        if (starCount > 0) return 8; // any pointer = 8 bytes (64-bit)

        // Find base type keyword (scan backward)
        for (var i = end - 1; i >= 0; i--)
        {
            var text = tokens[i].Content?.ToString() ?? "";
            if (TypeKeywords.Contains(text))
            {
                return text switch
                {
                    "void" => 1, "char" or "_Bool" => 1, "short" => 2,
                    "int" => 4, "long" => 8, "float" => 4, "double" => 8,
                    _ => null
                };
            }
            if (tokens[i].ID == _typeNameSym)
            {
                var tn = tokens[i].Content?.ToString() ?? "";
                return _typedefSizes.TryGetValue(tn, out var ts) ? ts : null;
            }
            if (tokens[i].ID == _structSym || tokens[i].ID == _unionSym || tokens[i].ID == _enumSym)
                return null; // need layout info
        }
        return null;
    }
}
