#nullable enable

using System.Collections.Generic;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Comptime reflection — <c>@typeInfo(T)</c> and the <c>switch</c> over it, folded at
/// LOWERING time (road-to-zig-std S5). The <c>std.builtin.Type</c> value is synthesized directly
/// from the resolved <see cref="CType"/>, never by compiling <c>std/lang.zig</c> for its layout:
/// the union is compiler-known, exactly as the real compiler treats it (its own doc comment says
/// so). Nothing here reaches the IR — a folded field is a literal, a folded <c>switch</c> lowers
/// only the taken prong — so the comptime interpreter keeps its value/type firewall (no
/// <c>TypeVal</c>), the W1/W4 lowering-tier tradition the whole wall was built on.
///
/// <para><b>The fidelity rule.</b> A field is answered only when it is EXACTLY recoverable.
/// <c>signedness</c>, <c>child</c>, <c>is_const</c> and <c>len</c> are read straight off the
/// <see cref="CType"/> and are always right. <c>bits</c> is NOT: dotcc widens an arbitrary-width
/// <c>uN</c>/<c>iN</c> to the smallest standard width (<c>u21</c> → a 32-bit <c>uint</c>), so the
/// lowered type no longer knows it was declared with 21. It is therefore answered from the SOURCE
/// SPELLING — the same rule <c>@typeName</c> already follows — which S5b then made travel WITH a
/// type binding (see <see cref="_declaredIntBits"/>), so a <c>comptime T: type</c> param answers it
/// too. Only an <c>anytype</c> param / <c>@TypeOf(expr)</c>, where the type is inferred from a
/// VALUE and no spelling survives, is still refused. A silently-32 answer where zig says 21 is the
/// one outcome worth refusing.</para>
///
/// <para>This file covers the scalar kinds and the tag of every kind, so a <c>switch</c> over
/// <c>@typeInfo(T)</c> dispatches on ANY type. The aggregate kinds' member LISTS
/// (<c>field_names</c> / <c>field_types</c> / <c>field_values</c>) live in
/// <c>ZigLowering.TypeInfo.Lists.cs</c> (S5c).</para>
///
/// <para><b>Version note.</b> dotcc models zig <b>0.17-dev</b>'s <c>std.builtin.Type</c> — the
/// version whose std the campaign compiles from source. 0.16 and earlier expose
/// <c>fields</c>/<c>decls</c> as slices of field STRUCTS instead of the parallel arrays; the CI
/// oracle pins 0.16.0 (the newest DURABLE tag — dev tarballs are GC'd off the download index), so
/// the member-list surface is validated by emit pins plus a by-hand 0.17-dev run rather than a CI
/// differential. See the note in <c>ZigOracleTests</c>.</para></summary>
internal sealed partial class ZigLowering
{
    /// <summary>A folded <c>@typeInfo(T)</c> value: the active <c>std.builtin.Type</c> union TAG
    /// (<c>int</c>, <c>pointer</c>, <c>@"struct"</c> — the quoted tags arrive here already folded to
    /// their inner text by <see cref="NormalizeIdent"/>), the type it describes, and the declared bit
    /// width when the source spelling was available at the <c>@typeInfo</c> site (see the fidelity
    /// rule on the class). One record serves both the whole union value and the payload of its active
    /// field — <c>@typeInfo(T)</c>, <c>@typeInfo(T).int</c> and the <c>|i|</c> captured by
    /// <c>.int =&gt; |i|</c> are the same folded thing, which is what lets one map bind all three.</summary>
    private sealed record ZigTypeInfo(string Tag, CType Type, int? DeclaredBits);

    /// <summary>Each name bound to a folded <c>@typeInfo</c> value — a <c>const i = @typeInfo(T);</c>
    /// or <c>const i = @typeInfo(T).int;</c> (no runtime decl is emitted: the value is comptime-only),
    /// and the <c>|i|</c> capture of a folded comptime <c>switch</c> prong. Name-keyed and
    /// function-flat like <see cref="_comptimeValues"/> / <see cref="_typeAliases"/> (the W1
    /// leniency); the switch capture shadow-saves through <see cref="_typeInfoShadows"/> so a prong's
    /// binding does not leak past its arm.</summary>
    private readonly Dictionary<string, ZigTypeInfo> _typeInfoBindings = new(System.StringComparer.Ordinal);

    /// <summary>Name → previous <see cref="_typeInfoBindings"/> entry shadowed by a folded comptime
    /// <c>switch</c> prong's <c>|i|</c> capture, restored when the arm is done — the proven W2/W3b
    /// shadow pattern (<see cref="_typeAliasShadows"/>) applied to the reflection bindings.</summary>
    private readonly List<(string Name, ZigTypeInfo? Prev)> _typeInfoShadows = new();

    /// <summary>Each type-binding name → the DECLARED bit width of the zig integer bound to it, when
    /// that width is not recoverable from the lowered type. dotcc widens an arbitrary-width
    /// <c>uN</c>/<c>iN</c> to the smallest standard width (<c>u21</c> → a 32-bit <c>uint</c>), so the
    /// `CType` alone cannot answer <c>@typeInfo(T).int.bits</c>; this rides ALONGSIDE
    /// <see cref="_typeAliases"/> — seeded, shadowed and restored in lockstep with it — so a
    /// <c>comptime T: type</c> param, an alias, and a reified generic all carry the width the source
    /// spelled. Deliberately NOT a property of <see cref="CType"/>: `Prim` has value equality, so a
    /// width-carrying `u21` would stop comparing equal to `u32` and change coercion, peer typing and
    /// every memoization key in the front end — a far larger blast radius than this one question
    /// warrants.</summary>
    private readonly Dictionary<string, int> _declaredIntBits = new(System.StringComparer.Ordinal);

    /// <summary>Set or clear a name's declared integer width — the write half of
    /// <see cref="_declaredIntBits"/>, used both to seed a binding and to restore a shadowed one, so
    /// "no recorded width" and "width W" round-trip through the same call.</summary>
    private void SetDeclaredIntBits(string name, int? bits)
    {
        if (bits is { } b) { _declaredIntBits[name] = b; } else { _declaredIntBits.Remove(name); }
    }

    /// <summary>The declared bit width of a type ARGUMENT's AST — its own spelling
    /// (<see cref="DeclaredBitsFromSpelling"/>) when it is a primitive keyword, else the width already
    /// recorded for the name it references, so a width survives a chain of bindings
    /// (<c>const I = u21; f(I)</c> keys and answers exactly as <c>f(u21)</c> does).</summary>
    private int? DeclaredBitsOfTypeArg(Item typeAst)
    {
        if (DeclaredBitsFromSpelling(typeAst) is { } spelled) { return spelled; }
        var cur = typeAst;
        while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
        return cur.Content is Zig.Ident id && _declaredIntBits.TryGetValue(Tok(id.Arg0), out var bound)
            ? bound
            : null;
    }

    /// <summary>The <c>std.builtin.Type</c> union tag for a resolved type — what a
    /// <c>switch (@typeInfo(T))</c> prong matches. Tags are spelled as
    /// <see cref="NormalizeIdent"/> leaves them, so the quoted source forms <c>.@"struct"</c> /
    /// <c>.@"enum"</c> / <c>.@"union"</c> / <c>.@"fn"</c> compare as their inner text. A slice is
    /// zig's <c>pointer</c> kind (with <c>size</c> <c>.slice</c>), matching how the real
    /// <c>@typeInfo</c> reports one.</summary>
    private string TypeInfoTag(CType type) => type.Unqualified switch
    {
        // `_Bool` is an INTEGER prim in the C type model (CType.Bool == Prim("_Bool", 1, true, false)),
        // so it must be recognized before the integer arm or every `bool` would report `int`.
        CType.Prim { Name: "_Bool" } => "bool",
        CType.Prim { Integer: true } => "int",
        CType.Prim => "float",
        CType.VoidType => "void",
        CType.Pointer or CType.Slice => "pointer",
        CType.Optional => "optional",
        CType.Array => "array",
        CType.Enum => "enum",
        CType.Func => "fn",
        CType.ErrorUnion => "error_union",
        CType.ErrorSetType => "error_set",
        // A named aggregate is a struct unless it was registered as a `union(enum)`; the tuple /
        // curated-runtime types (ArrayList, Allocator) are structs in zig too.
        CType.Named n when _unions.ContainsKey(n.Name) => "union",
        CType.Named or CType.Tuple or CType.ZigList or CType.Allocator => "struct",
        _ => throw new IrUnsupportedException(
            $"zig `@typeInfo`: no `std.builtin.Type` tag is modeled for {type.Describe()} (road-to-zig-std S5)"),
    };

    /// <summary>The DECLARED bit width read off a type's SOURCE spelling, or null when the operand is
    /// not a bare primitive keyword (an alias, a comptime type param, <c>@TypeOf(x)</c>, a composite).
    /// This is the only width that is safe to report as <c>@typeInfo(T).int.bits</c> — see the
    /// fidelity rule on the class. The <c>uN</c>/<c>iN</c> rule matches
    /// <see cref="TryArbitraryWidthInt"/>'s (that is where the widening this guards against happens);
    /// the fixed names are dotcc's LP64 target widths, which agree with zig's for this target.</summary>
    private static int? DeclaredBitsFromSpelling(Item typeAst)
    {
        if (typeAst.Content is Zig.Grouped g) { return DeclaredBitsFromSpelling(g.Arg1); }
        if (typeAst.Content is not Zig.Ident id) { return null; }
        var name = Tok(id.Arg0);
        switch (name)
        {
            case "usize" or "isize" or "c_long" or "c_ulong" or "c_longlong" or "c_ulonglong" or "f64": return 64;
            case "c_int" or "c_uint" or "f32": return 32;
            case "c_short" or "c_ushort": return 16;
            case "c_char": return 8;
        }
        // `uN` / `iN` — the declared width verbatim, which is exactly what the lowered type loses.
        if (name.Length < 2 || (name[0] != 'u' && name[0] != 'i')) { return null; }
        var digits = name.AsSpan(1);
        if (digits.Length > 1 && digits[0] == '0') { return null; }
        foreach (var ch in digits) { if (ch is < '0' or > '9') { return null; } }
        return int.TryParse(digits, out var bits) && bits >= 1 && bits <= 128 ? bits : null;
    }

    /// <summary>Evaluate an expression to a folded <c>@typeInfo</c> value. Four forms, all comptime:
    /// the <c>@typeInfo(T)</c> builtin itself, a parenthesized one, a name bound to one
    /// (<see cref="_typeInfoBindings"/> — a <c>const</c> or a folded prong capture), and a payload
    /// access <c>&lt;info&gt;.int</c> that names the ACTIVE tag. Naming an inactive tag
    /// (<c>@typeInfo(u8).float</c>) is a loud error, exactly as zig rejects reading an inactive union
    /// field. Non-throwing for a non-<c>@typeInfo</c> expression (returns false) so every caller can
    /// probe speculatively before its own lowering.</summary>
    private bool TryEvalTypeInfo(Item expr, out ZigTypeInfo info)
    {
        info = null!;
        switch (expr.Content)
        {
            case Zig.Grouped g:
                return TryEvalTypeInfo(g.Arg1, out info);

            // A name bound to a folded value. Guarded on the name NOT resolving to a real symbol: the
            // bindings are function-flat (the W1 leniency), so without this a `const info = @typeInfo(T);`
            // in one function would shadow an ordinary local named `info` in another — and a
            // `switch (info)` there would fold instead of lowering. A dropped comptime binding declares
            // no symbol, so the guard never rejects a genuine one.
            case Zig.Ident id when _symbols.Resolve(Tok(id.Arg0)) is null
                                   && _typeInfoBindings.TryGetValue(Tok(id.Arg0), out var bound):
                info = bound;
                return true;

            case Zig.BuiltinCall b when Tok(b.Arg0) == "@typeInfo":
            {
                var args = Flatten(b.Arg2);
                if (args.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `@typeInfo` expects (type); got {args.Count} argument(s)");
                }
                var t = LowerType(args[0]);
                info = new ZigTypeInfo(TypeInfoTag(t), t, DeclaredBitsOfTypeArg(args[0]));
                return true;
            }

            // `<info>.int` — the payload of the ACTIVE union field. Yields the same folded record (the
            // payload and the union value describe one type), after checking the tag really is active.
            case Zig.Field f when TryEvalTypeInfo(f.Arg0, out var baseInfo):
            {
                var tag = Tok(f.Arg2);
                if (tag == baseInfo.Tag) { info = baseInfo; return true; }
                // Not a tag at all → let the caller try a payload FIELD (`.bits`, `.child`, …).
                if (!IsTypeInfoTag(tag)) { return false; }
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({baseInfo.Type.Describe()}).{tag}`: the active `std.builtin.Type` field is "
                    + $"`{baseInfo.Tag}`, not `{tag}`");
            }
        }
        return false;
    }

    /// <summary>True for a name that is a <c>std.builtin.Type</c> union TAG rather than a payload
    /// field — the set <see cref="TypeInfoTag"/> can produce, so <see cref="TryEvalTypeInfo"/> can
    /// tell "you named an inactive tag" (an error) from "you named a payload field" (fall through to
    /// the field fold).</summary>
    private static bool IsTypeInfoTag(string name) => name is
        "int" or "float" or "bool" or "void" or "pointer" or "optional" or "array"
        or "enum" or "struct" or "union" or "fn" or "error_union" or "error_set";

    /// <summary>Fold a <c>@typeInfo</c> payload field that yields a VALUE — <c>bits</c>,
    /// <c>is_const</c>, <c>len</c>. Returns false when the expression is not such an access, so the
    /// ordinary member lowering runs. A field that exists in zig but is not exactly recoverable here
    /// (<c>size</c>) or needs comptime aggregates (<c>fields</c>, <c>decls</c>) is a loud cut naming
    /// the brick that lands it. <c>signedness</c> is not a value: it is an enum literal, folded by
    /// <see cref="TryEvalComptimeTag"/> for the <c>==</c> / <c>switch</c> positions it is actually
    /// used in.</summary>
    private bool TryFoldTypeInfoValue(Item expr, out CExpr value)
    {
        value = null!;
        if (expr.Content is not Zig.Field f || !TryEvalTypeInfo(f.Arg0, out var info)) { return false; }
        var field = Tok(f.Arg2);
        switch (info.Tag, field)
        {
            case ("int", "bits") or ("float", "bits"):
                if (info.DeclaredBits is not { } bits)
                {
                    throw new IrUnsupportedException(
                        $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.bits`: the declared width is not known here. "
                        + "It rides a type SPELLING — `@typeInfo(u21)` directly, or a `comptime T: type` / alias bound to "
                        + "one — but an `anytype` param or `@TypeOf(expr)` yields only the lowered type, and dotcc widens "
                        + "`uN`/`iN` to the smallest standard width (`u21` → a 32-bit `uint`), so reporting that width "
                        + "would disagree with zig. Spell the type, or pass it as a `comptime T: type`");
                }
                value = new LitInt(bits.ToString(System.Globalization.CultureInfo.InvariantCulture), bits) { Type = CType.Int };
                return true;

            case ("pointer", "is_const"):
                var pointee = info.Type.Unqualified switch
                {
                    CType.Pointer p => p.Pointee,
                    CType.Slice s => s.Element,
                    _ => CType.Void,
                };
                value = new LitBool(pointee.IsConst) { Type = CType.Bool };
                return true;

            case ("array", "len"):
                if (info.Type.Unqualified is not CType.Array { Count: { } count })
                {
                    throw new IrUnsupportedException(
                        $"zig `@typeInfo({info.Type.Describe()}).array.len`: the array has no comptime-known length");
                }
                // Typed `int`, not the `usize` zig gives `.len`, for the same reason BindFoldedCapture
                // narrows a folded capture: the literal then renders bare (`4`, not `4UL`) and C#'s
                // implicit CONSTANT conversion lets it land in any integer sink — a `4UL` would not
                // assign to a `u8`/`u16` annotation (CS0266). The value always fits.
                value = new LitInt(count.ToString(System.Globalization.CultureInfo.InvariantCulture), count) { Type = CType.Int };
                return true;

            // The member LISTS (road-to-zig-std S5c) fold in their own path — reaching here means one
            // was used as a bare VALUE, which it cannot be: a comptime list has no runtime form.
            case (_, "field_names") or (_, "field_types") or (_, "field_values"):
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.{field}` is a comptime member LIST with no runtime "
                    + "representation — read its `.len`, index it at a comptime index, walk it with `inline for` "
                    + "(a plain `for` cannot: there is nothing to iterate at runtime), or bind it to a `const`");

            // `fields` / `decls` are the OLDER `std.builtin.Type` shape (a slice of field STRUCTS). The
            // pinned zig replaced them with the parallel `field_names` / `field_types` / `field_values`
            // arrays, so pointing at those is more useful than implementing a shape zig no longer has.
            case (_, "fields") or (_, "decls"):
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.{field}`: dotcc models zig 0.17-dev's "
                    + "`std.builtin.Type`, which replaced the `fields`/`decls` slice-of-structs with the parallel "
                    + "arrays `field_names` / `field_types` / `field_values` — use those. (Zig 0.16 and earlier "
                    + "spell it the old way; dotcc tracks 0.17-dev, the version whose std it compiles.)");

            case ("pointer", "size"):
                throw new IrUnsupportedException(
                    "zig `@typeInfo(T).pointer.size`: dotcc lowers `*T`, `[*]T` and `[*c]T` to one C pointer, so the "
                    + "pointer SIZE class is not recoverable from the lowered type (road-to-zig-std S5)");

            case (_, "signedness"):
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.signedness`: only an `int` carries a signedness");

            case ("int", "child") or ("float", "child") or ("bool", "child") or ("void", "child"):
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.child`: a scalar kind has no child type");
        }
        // `.child` on a pointer/optional/array is a TYPE, not a value — handled by
        // TryFoldTypeInfoType from the type positions; reaching here means it was used as a value.
        if (field == "child")
        {
            throw new IrUnsupportedException(
                $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.child` is a TYPE — use it in a type position "
                + "(`const C = @typeInfo(T).pointer.child;`), not as a value");
        }
        throw new IrUnsupportedException(
            $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.{field}`: no such `std.builtin.Type` field is modeled "
            + "(road-to-zig-std S5)");
    }

    /// <summary>Fold a <c>@typeInfo</c> payload field that yields a TYPE — <c>child</c> on a pointer,
    /// slice, optional or array. Consulted from the TYPE positions (<see cref="LowerType"/> and the
    /// type-alias RHS), so <c>const C = @typeInfo(T).pointer.child;</c> and a <c>child</c>-typed
    /// annotation resolve. Returns false for any other expression.</summary>
    private bool TryFoldTypeInfoType(Item expr, out CType type)
    {
        type = CType.Void;
        if (expr.Content is Zig.Grouped g) { return TryFoldTypeInfoType(g.Arg1, out type); }
        if (expr.Content is not Zig.Field f || Tok(f.Arg2) != "child" || !TryEvalTypeInfo(f.Arg0, out var info))
        {
            return false;
        }
        var child = info.Type.Unqualified switch
        {
            CType.Pointer p => p.Pointee,
            CType.Slice s => s.Element,
            CType.Optional o => o.Inner,
            CType.Array a => a.Element,
            _ => null,
        };
        if (child is null)
        {
            throw new IrUnsupportedException(
                $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.child`: only a pointer / slice / optional / array "
                + "kind has a child type");
        }
        type = child;
        return true;
    }

    /// <summary>Evaluate an expression to a comptime enum-literal TAG — the two positions a tag is
    /// legal in: the subject of a <c>switch</c> and an <c>==</c>/<c>!=</c> against a <c>.name</c>
    /// literal. Two producers: a folded <c>@typeInfo</c> value (its union tag, so
    /// <c>switch (@typeInfo(T))</c> dispatches) and an <c>int</c> payload's <c>signedness</c>
    /// (<c>signed</c>/<c>unsigned</c>, which IS exactly recoverable — dotcc's width widening never
    /// changes a type's signedness). Yields the payload alongside, so a prong capture can bind it.</summary>
    private bool TryEvalComptimeTag(Item expr, out string tag, out ZigTypeInfo? payload)
    {
        tag = "";
        payload = null;
        if (expr.Content is Zig.Grouped g) { return TryEvalComptimeTag(g.Arg1, out tag, out payload); }
        // `<info>.signedness` — checked BEFORE the whole-value case so the field wins over the record.
        if (expr.Content is Zig.Field f && Tok(f.Arg2) == "signedness" && TryEvalTypeInfo(f.Arg0, out var sInfo))
        {
            if (sInfo.Tag != "int")
            {
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({sInfo.Type.Describe()}).{sInfo.Tag}.signedness`: only an `int` carries a signedness");
            }
            tag = sInfo.Type.Unqualified is CType.Prim { Signed: true } ? "signed" : "unsigned";
            return true;
        }
        if (TryEvalTypeInfo(expr, out var info))
        {
            tag = info.Tag;
            payload = info;
            return true;
        }
        return false;
    }

    /// <summary>Fold <c>&lt;comptime tag&gt; == .name</c> / <c>!=</c> to a boolean literal — the
    /// non-<c>switch</c> half of how a tag is consumed (<c>info.signedness == .unsigned</c>). Either
    /// operand may be the tag. Returns false when this is not a tag comparison, so the ordinary
    /// binary lowering runs.</summary>
    private bool TryFoldComptimeTagCompare(Item leftItem, Item rightItem, bool negate, out CExpr value)
    {
        value = null!;
        string? tag = null;
        string? lit = null;
        if (TryEvalComptimeTag(leftItem, out var lt, out _) && rightItem.Content is Zig.EnumLit rel)
        {
            tag = lt;
            lit = Tok(rel.Arg1);
        }
        else if (TryEvalComptimeTag(rightItem, out var rt, out _) && leftItem.Content is Zig.EnumLit lel)
        {
            tag = rt;
            lit = Tok(lel.Arg1);
        }
        if (tag is null || lit is null) { return false; }
        var equal = tag == lit;
        value = new LitBool(negate ? !equal : equal) { Type = CType.Bool };
        return true;
    }

    /// <summary>One decomposed <c>switch</c> prong: its case-value list, an optional payload capture,
    /// and exactly one body form (a braced block, a bare value expression, or a <c>return</c>). The
    /// shape the comptime fold needs in all three switch positions (statement, expression, and the
    /// value-temp filler) without each re-deriving it from the eight capture productions.</summary>
    private sealed record ZigProng(Item CaseVals, string? CaptureName, Item? Block, Item? Expr, Item? Return, bool ReturnsVoid);

    /// <summary>Decompose a prong into <see cref="ZigProng"/>. A by-reference capture
    /// (<c>|*x|</c>) is rejected: a comptime <c>@typeInfo</c> value has no storage to point at.</summary>
    private static ZigProng DecomposeProng(Item prong) => prong.Content switch
    {
        Zig.Prong p                  => new ZigProng(p.Arg0, null,         p.Arg2, null,   null,   false),
        Zig.ProngExpr p              => new ZigProng(p.Arg0, null,         null,   p.Arg2, null,   false),
        Zig.ProngReturn p            => new ZigProng(p.Arg0, null,         null,   null,   p.Arg3, false),
        Zig.ProngReturnVoid p        => new ZigProng(p.Arg0, null,         null,   null,   null,   true),
        Zig.ProngCapture p           => new ZigProng(p.Arg0, Tok(p.Arg3),  p.Arg5, null,   null,   false),
        Zig.ProngCaptureExpr p       => new ZigProng(p.Arg0, Tok(p.Arg3),  null,   p.Arg5, null,   false),
        Zig.ProngCaptureReturn p     => new ZigProng(p.Arg0, Tok(p.Arg3),  null,   null,   p.Arg6, false),
        Zig.ProngCaptureReturnVoid p => new ZigProng(p.Arg0, Tok(p.Arg3),  null,   null,   null,   true),
        Zig.ProngCaptureRef or Zig.ProngCaptureRefExpr or Zig.ProngCaptureRefReturn or Zig.ProngCaptureRefReturnVoid
            => throw new IrUnsupportedException(
                "zig `switch (@typeInfo(T)) { … => |*x| … }`: a comptime `std.builtin.Type` value has no storage, so it "
                + "cannot be captured by reference"),
        _ => throw new IrUnsupportedException("zig switch prong: " + (prong.Content?.GetType().Name ?? "null")),
    };

    /// <summary>Select the prong a <c>switch</c> over a COMPTIME TAG takes, or null when the subject
    /// is not comptime-known (the caller then lowers an ordinary runtime switch). The tag is matched
    /// against each prong's <c>.name</c> case values, with an <c>else</c> prong as the fallback —
    /// only the selected prong is then lowered, so the arms that do not apply never instantiate a
    /// generic, never demand a field the type does not have, and never fail to compile. That is the
    /// whole point of the fold: <c>switch (@typeInfo(T))</c> is how std asks "which kind is T", and
    /// every non-taken arm is written for a different kind.</summary>
    private ZigProng? SelectComptimeProng(Item subjectItem, Item prongsItem, out ZigTypeInfo? payload)
    {
        if (!TryEvalComptimeTag(subjectItem, out var tag, out payload)) { return null; }
        ZigProng? elseProng = null;
        foreach (var prongItem in Flatten(prongsItem))
        {
            var prong = DecomposeProng(prongItem);
            if (prong.CaseVals.Content is Zig.CaseElse) { elseProng = prong; continue; }
            foreach (var (lo, hi) in WalkCaseValItems(prong.CaseVals))
            {
                if (hi is not null)
                {
                    throw new IrUnsupportedException(
                        "zig `switch (@typeInfo(T))`: a `lo...hi` range is not a tag — prongs match `.int` / `.pointer` / "
                        + "`.@\"struct\"` literals or `else`");
                }
                if (lo.Content is not Zig.EnumLit el)
                {
                    throw new IrUnsupportedException(
                        "zig `switch (@typeInfo(T))`: a prong's case value must be a `.tag` enum literal or `else`");
                }
                if (Tok(el.Arg1) == tag) { return prong; }
            }
        }
        if (elseProng is not null) { return elseProng; }
        throw new IrUnsupportedException(
            $"zig `switch` over a comptime `.{tag}`: no prong matches it and there is no `else` "
            + "(real zig would reject the switch as non-exhaustive)");
    }

    /// <summary>Bind a folded prong's <c>|i|</c> capture to the switch subject's payload for the
    /// duration of that arm, shadow-saving any previous binding of the name. <c>_</c> binds nothing;
    /// a capture on a subject with no payload (a bare <c>signedness</c> tag) is a loud cut. Paired
    /// with <see cref="ExitComptimeProng"/>.</summary>
    private void EnterComptimeProng(ZigProng prong, ZigTypeInfo? payload)
    {
        // An empty name is the "bound nothing" marker, so Enter/Exit always pair one-for-one whether or
        // not this prong actually captures.
        _typeInfoShadows.Add(("", null));
        if (prong.CaptureName is not { } name || name == "_") { return; }
        if (payload is null)
        {
            throw new IrUnsupportedException(
                $"zig `switch` prong capture `|{name}|`: this comptime tag carries no payload to bind");
        }
        _typeInfoShadows[^1] = (name, _typeInfoBindings.GetValueOrDefault(name));
        _typeInfoBindings[name] = payload;
    }

    /// <summary>Restore what <see cref="EnterComptimeProng"/> shadowed.</summary>
    private void ExitComptimeProng()
    {
        var (name, prev) = _typeInfoShadows[^1];
        _typeInfoShadows.RemoveAt(_typeInfoShadows.Count - 1);
        if (name.Length == 0) { return; }
        if (prev is { } p) { _typeInfoBindings[name] = p; } else { _typeInfoBindings.Remove(name); }
    }
}
