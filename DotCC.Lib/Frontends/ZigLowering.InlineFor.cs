#nullable enable

using System.Collections.Generic;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary><c>inline for</c> over a COMPTIME LIST (road-to-zig-std S6) — the consumer S5c's member
/// lists were built for. A <c>@typeInfo</c> member list has no runtime representation, so only an
/// <c>inline</c> loop can walk one: each iteration is unrolled at lowering time with the capture
/// bound as a COMPTIME binding, not as a runtime <c>const</c>.
///
/// <para>That is the whole difference from <see cref="UnrollInlineFor"/>, which serves the counted
/// range and the fixed <c>[N]T</c> array: those bind the capture to a runtime symbol initialized by
/// an emitted <c>const cap = …;</c>. A list element is a comptime STRING, a TYPE, or a
/// comptime_int — a type has no runtime slot at all, and a name used as <c>@field(x, name)</c> must
/// be readable at lowering time — so the capture is seeded into the same maps an ordinary comptime
/// binding uses (<see cref="_comptimeValues"/> / <see cref="_typeAliases"/>) and shadow-restored
/// after each copy, exactly as W3b seeds a type parameter.</para>
///
/// <para><b>The shapes are measured, not guessed.</b> In the pinned std the PARALLEL two-list form
/// dominates: <c>inline for (info.field_names, info.field_types) |field_name, field_type|</c> ×17,
/// against ×11 for the single-list form, ×12 for <c>(list, 0..) |x, i|</c>, and ×21 for a
/// <c>[_]type{…}</c> literal walked as <c>|T|</c>. All four are served here by one unroll over N
/// index-parallel lists — the index form by treating <c>0..</c> as a synthesized list of its own
/// indices, so there is no second code path.</para></summary>
internal sealed partial class ZigLowering
{
    /// <summary>Each name bound to a comptime STRING, as its plain (unquoted, decoded) text — the
    /// comptime-NAME half of a string binding, next to the value half in
    /// <see cref="_comptimeValues"/>. Two maps because the two positions need different things: a
    /// value position needs the encoded <see cref="LitStr"/>, while <c>@field(x, name)</c> /
    /// <c>@hasField(T, name)</c> need the member name as text. Populated only by a comptime capture
    /// today (a source string literal is read straight off its AST).</summary>
    private readonly Dictionary<string, string> _comptimeStrings = new(System.StringComparer.Ordinal);

    /// <summary>What a comptime capture displaced when it bound its name, so the binding can be
    /// undone after the copy. <c>null</c> means "the name was not bound in that domain" — no
    /// separate presence flag is needed, since neither map can hold a null.</summary>
    private readonly record struct ComptimeCaptureShadow(
        string Name, CExpr? Value, string? Text, CType? Alias, int? Bits);

    /// <summary>Is <paramref name="operand"/> a comptime-iterable list — a <c>@typeInfo</c> member
    /// list (or a name bound to one), or a <c>[_]type{…}</c> literal? Returns false for anything
    /// else, so an ordinary runtime <c>for</c> operand falls through to the existing lowering.</summary>
    private bool TryComptimeIterable(Item operand, out ZigComptimeList list)
    {
        if (TryFoldTypeInfoList(operand, out list)) { return true; }
        if (TypeListLiteral(operand) is { } literal) { list = literal; return true; }
        list = null!;
        return false;
    }

    /// <summary>A <c>[_]type{ f32, f64 }</c> / <c>[N]type{…}</c> literal read as a comptime list of
    /// TYPES (×21 in the pinned std, the commonest <c>|T|</c> source). Recognized structurally on the
    /// array type's element spelling being the bare word <c>type</c>, because such a literal has no
    /// runtime element type to lower — <c>[_]type</c> is not a <see cref="CType"/> at all. The
    /// leading-dot anonymous forms (<c>.{ f32, f64 }</c>, <c>&amp;.{…}</c>) need sink inference and
    /// are not recognized; they reach the ordinary path and its error.</summary>
    private ZigComptimeList? TypeListLiteral(Item operand)
    {
        if (operand.Content is Zig.Grouped g) { return TypeListLiteral(g.Arg1); }
        if (operand.Content is not Zig.TypedStructInit init
            || init.Arg0.Content is not Zig.TyArray arr
            || arr.Arg3.Content is not Zig.Ident elem
            || Tok(elem.Arg0) != "type")
        {
            return null;
        }
        var types = new List<CType>();
        foreach (var item in Flatten(init.Arg2))
        {
            // Positional only — `[_]type{ .a = u8 }` is not a thing, and a named element here would
            // otherwise silently drop.
            if (item.Content is not Zig.FieldInitPositional pos)
            {
                throw new IrUnsupportedException(
                    "zig `[_]type{…}` must list types positionally (`[_]type{ u8, u16 }`)");
            }
            types.Add(LowerType(pos.Arg0));
        }
        return new ZigComptimeList { Label = "[_]type{…}", Types = types };
    }

    /// <summary>The indices <c>0, 1, …, count-1</c> as a comptime list, so <c>for (list, 0..) |x, i|</c>
    /// is just a second index-parallel operand rather than a special case in the unroll.</summary>
    private static ZigComptimeList IndexList(int count)
        => new() { Label = "0..", Ints = Enumerable.Range(0, count).Select(i => (long)i).ToList() };

    /// <summary>Bind one capture to element <paramref name="k"/> of <paramref name="list"/> and return
    /// what it displaced. The list is homogeneous, so the capture binds in exactly ONE domain — a
    /// string in <see cref="_comptimeValues"/> + <see cref="_comptimeStrings"/>, a type in
    /// <see cref="_typeAliases"/>, an integer in <see cref="_comptimeValues"/>. Every domain is
    /// cleared first: the maps are name-keyed and function-flat, so an unrelated outer binding of the
    /// same name in a DIFFERENT domain would otherwise stay visible inside the body and answer a
    /// lookup the capture should have shadowed.</summary>
    private ComptimeCaptureShadow SeedComptimeCapture(string name, ZigComptimeList list, int k)
    {
        var shadow = new ComptimeCaptureShadow(
            name,
            _comptimeValues.TryGetValue(name, out var prevValue) ? prevValue : null,
            _comptimeStrings.TryGetValue(name, out var prevText) ? prevText : null,
            _typeAliases.TryGetValue(name, out var prevAlias) ? prevAlias : null,
            _declaredIntBits.TryGetValue(name, out var prevBits) ? prevBits : null);

        _comptimeValues.Remove(name);
        _comptimeStrings.Remove(name);
        _typeAliases.Remove(name);
        _declaredIntBits.Remove(name);

        if (list.Strings is { } strings)
        {
            _comptimeValues[name] = ZigStringLiteral(strings[k]);
            _comptimeStrings[name] = strings[k];
        }
        else if (list.Types is { } types)
        {
            _typeAliases[name] = types[k];
        }
        else if (list.Ints is { } ints)
        {
            // `int`, not the element's own width — the same choice `.len` and `bits` made in S5c: a
            // bare `int` literal converts implicitly into any integer sink, while a suffixed one
            // (`4UL`) fails CS0266 into a narrower one.
            _comptimeValues[name] = new LitInt(
                ints[k].ToString(System.Globalization.CultureInfo.InvariantCulture), ints[k]) { Type = CType.Int };
        }
        return shadow;
    }

    /// <summary>Undo <see cref="SeedComptimeCapture"/>: restore each domain to what it held, or remove
    /// the name where it held nothing. Seed and restore are exact inverses, so nested
    /// <c>inline for</c>s over the same capture name nest correctly.</summary>
    private void RestoreComptimeCapture(ComptimeCaptureShadow shadow)
    {
        if (shadow.Value is { } v) { _comptimeValues[shadow.Name] = v; } else { _comptimeValues.Remove(shadow.Name); }
        if (shadow.Text is { } t) { _comptimeStrings[shadow.Name] = t; } else { _comptimeStrings.Remove(shadow.Name); }
        if (shadow.Alias is { } a) { _typeAliases[shadow.Name] = a; } else { _typeAliases.Remove(shadow.Name); }
        SetDeclaredIntBits(shadow.Name, shadow.Bits);
    }

    /// <summary>Unroll an <c>inline for</c> over one or more index-parallel comptime lists: for each
    /// index, seed every capture, lower a fresh copy of the body, then restore. The loop vanishes —
    /// what remains is straight-line IR whose comptime bindings have already been folded away, so an
    /// empty list correctly produces no code at all.
    ///
    /// <para>Unlike <see cref="UnrollInlineFor"/> no <c>const</c> is emitted per copy: a comptime
    /// capture has no runtime slot. The body is still wrapped in a block so sibling copies get
    /// distinct scopes for any locals they declare.</para></summary>
    private CStmt UnrollComptimeFor(IReadOnlyList<(ZigComptimeList List, string Capture)> binds, Item bodyItem)
    {
        var count = binds[0].List.Count;
        foreach (var (list, _) in binds)
        {
            if (list.Count != count)
            {
                throw new IrUnsupportedException(
                    $"`inline for` over parallel lists requires equal lengths — `{binds[0].List.Label}` has {count} "
                    + $"element(s) and `{list.Label}` has {list.Count} (zig requires this too)");
            }
        }
        if (count > InlineUnrollCap)
        {
            throw new IrUnsupportedException(
                $"`inline for` would unroll {count} iterations, exceeding the cap ({InlineUnrollCap})");
        }
        var copies = new List<CStmt>(count);
        for (var k = 0; k < count; k++)
        {
            var shadows = new List<ComptimeCaptureShadow>(binds.Count);
            foreach (var (list, capture) in binds) { shadows.Add(SeedComptimeCapture(capture, list, k)); }
            _symbols.EnterScope();
            var body = LowerStmt(bodyItem);
            _symbols.ExitScope();
            // Restore innermost-first, so two captures sharing a name (`|x, x|`, which zig rejects but
            // which must not corrupt the maps here) unwind in the order they were seeded.
            for (var i = shadows.Count - 1; i >= 0; i--) { RestoreComptimeCapture(shadows[i]); }
            if (HasLoopEscape(body))
            {
                throw new IrUnsupportedException(
                    "`break`/`continue` inside an `inline for` body is not supported yet (the loop is "
                    + "unrolled, so there is no enclosing loop to target)");
            }
            copies.Add(new Block(new List<CStmt> { body }));
        }
        return new Seq(copies);
    }

    /// <summary>Read a comptime STRING argument — a source string literal, or a name bound to one by
    /// an <c>inline for</c> capture. This is what lets <c>@field(x, field_name)</c> and
    /// <c>@hasField(T, field_name)</c> work INSIDE an unrolled loop, which is the whole point of
    /// iterating <c>field_names</c>. Returns null when the argument is neither, so each caller keeps
    /// its own "must be a comptime string" error.</summary>
    private string? ComptimeStringArg(Item item)
    {
        switch (item.Content)
        {
            case Zig.Grouped g:
                return ComptimeStringArg(g.Arg1);
            case Zig.StrLit s:
                return UnquoteStringLiteral(Tok(s.Arg0));
            // Guarded on the name NOT naming a real symbol, for the same reason every other
            // name-keyed comptime lookup here is: the map is function-flat.
            case Zig.Ident id when _symbols.Resolve(Tok(id.Arg0)) is null
                                   && _comptimeStrings.TryGetValue(Tok(id.Arg0), out var text):
                return text;
            default:
                return null;
        }
    }
}
