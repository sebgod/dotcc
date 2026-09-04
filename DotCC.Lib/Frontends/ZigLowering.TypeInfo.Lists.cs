#nullable enable

using System.Collections.Generic;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Comptime reflection, the AGGREGATE half (road-to-zig-std S5c): the parallel member
/// lists a <c>std.builtin.Type</c> payload carries — <c>field_names</c>, <c>field_types</c>,
/// <c>field_values</c> — plus <c>tag_type</c>.
///
/// <para><b>The pinned zig made this far smaller than the plan assumed.</b> The plan was written
/// against the older <c>fields: []const StructField</c> API — a comptime slice of comptime STRUCTS,
/// which really would need a general aggregate value domain. This zig (0.17.0-dev.667) instead
/// exposes PARALLEL ARRAYS: <c>field_names: []const [:0]const u8</c>, <c>field_types: []const
/// type</c>, <c>field_values: []const comptime_int</c>. Each is homogeneous, so what is needed is a
/// comptime LIST of one element kind, not structs-in-slices. Measured against the pinned source,
/// that is also where the uses are: <c>.field_names.len</c> ×167, <c>.field_names[i]</c> ×34.</para>
///
/// <para>Everything folds at the LOWERING tier like the rest of S5 — the lists are built from
/// dotcc's own registries (<see cref="IrBuilder.StructFieldsOf"/> for a struct/union's declared
/// fields in order, <see cref="IrBuilder.Enums"/> for an enum's members in order), never by
/// compiling <c>std/lang.zig</c>. No list reaches the IR: it is consumed by <c>.len</c>, by a
/// comptime index, or by a binding, each of which folds to a literal or a type.</para></summary>
internal sealed partial class ZigLowering
{
    /// <summary>A comptime LIST value — one of <c>std.builtin.Type</c>'s parallel member arrays.
    /// Homogeneous by construction: exactly one projection is non-null, mirroring the shape the
    /// pinned zig declares. <see cref="Label"/> is the source spelling, carried so a diagnostic can
    /// name what the reader actually wrote.</summary>
    private sealed record ZigComptimeList
    {
        public required string Label { get; init; }
        public IReadOnlyList<string>? Strings { get; init; }
        public IReadOnlyList<CType>? Types { get; init; }
        public IReadOnlyList<long>? Ints { get; init; }

        public int Count => Strings?.Count ?? Types?.Count ?? Ints?.Count ?? 0;
    }

    /// <summary>Each name bound to a folded member list (<c>const names = @typeInfo(T).@"struct"
    /// .field_names;</c>). No runtime decl is emitted, exactly as for
    /// <see cref="_typeInfoBindings"/>: a comptime list has no runtime representation. Same
    /// function-flat leniency, same symbol guard.</summary>
    private readonly Dictionary<string, ZigComptimeList> _typeInfoLists = new(System.StringComparer.Ordinal);

    /// <summary>Enum names whose tag type the SOURCE spelled (<c>enum(u8) {…}</c>), as opposed to
    /// leaving it inferred. Only these answer <c>@typeInfo(E).@"enum".tag_type</c>: zig infers the
    /// smallest unsigned int that holds the largest member (<c>u2</c> for four members) while dotcc
    /// defaults an untyped enum to <c>int</c>, so answering an inferred tag would disagree with zig on
    /// the width and on <c>@sizeOf</c>. Same shape of judgement as `bits` needing a spelling.</summary>
    private readonly HashSet<string> _enumsWithSpelledTag = new(System.StringComparer.Ordinal);

    /// <summary>The declared FIELDS of a struct/union type in declaration order, or null when the
    /// type names no registered aggregate. Order is load-bearing: <c>field_names</c> and
    /// <c>field_types</c> are index-parallel, and zig guarantees declaration order.</summary>
    private IReadOnlyList<StructField>? FieldsOfAggregate(CType type)
        => type.Unqualified is CType.Named n ? _ir.StructFieldsOf(n.Name) : null;

    /// <summary>The MEMBERS of an enum type in declaration order, or null when the type is not a
    /// registered enum. Read off <see cref="IrBuilder.Enums"/> — an ordered list, unlike the
    /// name-keyed member-symbol map, which is exactly why this reads the IR registry.</summary>
    private IReadOnlyList<EnumMember>? MembersOfEnum(CType type)
        => type.Unqualified is CType.Enum e
            ? _ir.Enums.FirstOrDefault(d => d.Name == e.Name)?.Members
            : null;

    /// <summary>Fold a <c>@typeInfo</c> payload field that yields a member LIST — the parallel
    /// arrays <c>field_names</c> / <c>field_types</c> / <c>field_values</c>. Returns false when the
    /// expression is not such an access. A list asked of a kind that has none, and the member lists
    /// V1 does not model, are loud cuts naming exactly what is missing.</summary>
    private bool TryFoldTypeInfoList(Item expr, out ZigComptimeList list)
    {
        list = null!;
        if (expr.Content is Zig.Grouped gl) { return TryFoldTypeInfoList(gl.Arg1, out list); }
        // A name bound to a list — guarded on the name NOT naming a real symbol, for the same reason
        // TryEvalTypeInfo guards its binding lookup: the map is function-flat.
        if (expr.Content is Zig.Ident lid
            && _symbols.Resolve(Tok(lid.Arg0)) is null
            && _typeInfoLists.TryGetValue(Tok(lid.Arg0), out var bound))
        {
            list = bound;
            return true;
        }
        if (expr.Content is not Zig.Field f || !TryEvalTypeInfo(f.Arg0, out var info)) { return false; }
        var field = Tok(f.Arg2);
        switch (field)
        {
            case "field_names":
                if (FieldsOfAggregate(info.Type) is { } nf)
                {
                    list = new ZigComptimeList { Label = field, Strings = nf.Select(x => x.Name).ToList() };
                    return true;
                }
                if (MembersOfEnum(info.Type) is { } nm)
                {
                    list = new ZigComptimeList { Label = field, Strings = nm.Select(x => x.Name).ToList() };
                    return true;
                }
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.field_names`: only a registered struct / union / "
                    + "enum has declared fields");

            case "field_types":
                if (FieldsOfAggregate(info.Type) is { } tf)
                {
                    list = new ZigComptimeList { Label = field, Types = tf.Select(x => x.Type).ToList() };
                    return true;
                }
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.field_types`: only a struct / union has field "
                    + "types (an enum's members carry values — use `field_values`)");

            case "field_values":
                if (MembersOfEnum(info.Type) is { } vm)
                {
                    list = new ZigComptimeList { Label = field, Ints = vm.Select(x => x.Value).ToList() };
                    return true;
                }
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.field_values`: only an enum's members carry values");

            case "decl_names":
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.decl_names`: dotcc's container-const and method "
                    + "registries are name-keyed, so a DECLARATION-ORDER decl list is not available — `field_names` is "
                    + "(road-to-zig-std S5c). `@hasDecl` works, since membership needs no order");

            case "field_attrs":
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.field_attrs`: per-field alignment / default-value "
                    + "attributes are not modeled (road-to-zig-std S5c)");
        }
        return false;
    }

    /// <summary>Fold a use of a comptime member list: its <c>.len</c> (by a wide margin the commonest
    /// reflection operation in real std) or a comptime INDEX into it. Returns false when the
    /// expression is not such a use, so ordinary lowering runs. A STRING element folds to a string
    /// literal and an INTEGER element to an int literal; a TYPE element is a type, so it is served
    /// from the type positions by <see cref="TryFoldTypeInfoListType"/> and is a loud cut here.</summary>
    private bool TryFoldTypeInfoListValue(Item expr, out CExpr value)
    {
        value = null!;
        // `<list>.len`
        if (expr.Content is Zig.Field lf && Tok(lf.Arg2) == "len" && TryFoldTypeInfoList(lf.Arg0, out var lenList))
        {
            value = new LitInt(lenList.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), lenList.Count)
                { Type = CType.Int };
            return true;
        }
        // `<list>[i]` at a comptime index
        if (expr.Content is Zig.Index ix && TryFoldTypeInfoList(ix.Arg0, out var idxList))
        {
            var i = ComptimeListIndex(idxList, ix.Arg2);
            if (idxList.Strings is { } ss) { value = ZigStringLiteral(ss[i]); return true; }
            if (idxList.Ints is { } ii)
            {
                value = new LitInt(ii[i].ToString(System.Globalization.CultureInfo.InvariantCulture), ii[i]) { Type = CType.Int };
                return true;
            }
            throw new IrUnsupportedException(
                $"zig `{idxList.Label}[{i}]` is a TYPE — use it in a type position, not as a value");
        }
        return false;
    }

    /// <summary>Fold a comptime INDEX into a list of TYPES — the type-position counterpart of
    /// <see cref="TryFoldTypeInfoListValue"/> — plus the <c>tag_type</c> of an enum. Consulted from
    /// <see cref="LowerType"/> and the type-alias RHS.</summary>
    private bool TryFoldTypeInfoListType(Item expr, out CType type)
    {
        type = CType.Void;
        if (expr.Content is Zig.Grouped g) { return TryFoldTypeInfoListType(g.Arg1, out type); }
        // `@typeInfo(E).@"enum".tag_type` — the enum's underlying integer type. Not a list, but it
        // lives in the same payloads and is a TYPE like the element case below.
        if (expr.Content is Zig.Field tf && Tok(tf.Arg2) == "tag_type" && TryEvalTypeInfo(tf.Arg0, out var tinfo))
        {
            if (tinfo.Type.Unqualified is CType.Enum en)
            {
                // Only a SPELLED tag (`enum(u8) {…}`) is answered. zig INFERS one for an untyped enum —
                // the smallest unsigned int holding the largest member, `u2` for four members — while
                // dotcc defaults to `int`, so answering there would disagree with zig on the width and
                // on `@sizeOf(tag_type)`. A wrong type is worse than a named gap.
                if (!_enumsWithSpelledTag.Contains(en.Name))
                {
                    throw new IrUnsupportedException(
                        $"zig `@typeInfo({en.Name}).@\"enum\".tag_type`: this enum's tag type is INFERRED, and zig infers "
                        + "the smallest unsigned int that holds its largest member while dotcc defaults to `int` — "
                        + "reporting that would disagree on the width. Spell the tag (`enum(u8) {…}`) and it is answered "
                        + "(road-to-zig-std S5c)");
                }
                type = en.Underlying;
                return true;
            }
            throw new IrUnsupportedException(
                $"zig `@typeInfo({tinfo.Type.Describe()}).{tinfo.Tag}.tag_type`: dotcc models a tagged union's tag as a "
                + "synthesized enum that is not addressable as a type yet — a plain `enum`'s tag_type is "
                + "(road-to-zig-std S5c)");
        }
        if (expr.Content is not Zig.Index ix || !TryFoldTypeInfoList(ix.Arg0, out var list)) { return false; }
        // A non-TYPE element is not an error here: this runs speculatively from the type-alias probe
        // (`const first = names[0];`), where falling through lets the VALUE fold claim it. Only a
        // genuine type position reaches the ordinary resolver, which reports its own error.
        if (list.Types is not { } ts) { return false; }
        type = ts[ComptimeListIndex(list, ix.Arg2)];
        return true;
    }

    /// <summary>Fold <c>@hasField(T, "name")</c> / <c>@hasDecl(T, "name")</c> to a boolean literal
    /// (road-to-zig-std S5c; 73 + 220 uses in the pinned std). Both are MEMBERSHIP questions, so —
    /// unlike <c>decl_names</c> — they need no declaration order and read the name-keyed registries
    /// directly: fields/enum members for <c>@hasField</c>, container consts + methods for
    /// <c>@hasDecl</c>. Returns null when the builtin is something else, so the caller falls through.</summary>
    private LitBool? TryEvalMembershipBuiltin(Zig.BuiltinCall b)
    {
        var name = Tok(b.Arg0);
        if (name is not ("@hasField" or "@hasDecl")) { return null; }
        var args = Flatten(b.Arg2);
        if (args.Count != 2)
        {
            throw new IrUnsupportedException($"zig `{name}` expects (type, name); got {args.Count} argument(s)");
        }
        if (args[1].Content is not Zig.StrLit lit)
        {
            throw new IrUnsupportedException($"zig `{name}`: the member name must be a comptime string literal");
        }
        var member = UnquoteStringLiteral(Tok(lit.Arg0));
        var type = LowerType(args[0]);
        var present = name == "@hasField"
            ? (FieldsOfAggregate(type)?.Any(x => x.Name == member) ?? false)
              || (MembersOfEnum(type)?.Any(x => x.Name == member) ?? false)
            : HasDeclaredMember(type, member);
        return new LitBool(present) { Type = CType.Bool };
    }

    /// <summary>True when the container <paramref name="type"/> names declares <paramref name="member"/>
    /// as a <c>const</c> or a method — what <c>@hasDecl</c> asks. Container names are the registry keys
    /// for both tables, so this is a two-map membership test with no ordering involved.</summary>
    private bool HasDeclaredMember(CType type, string member)
    {
        if (ContainerTypeName(type) is not { } container) { return false; }
        return (_containerConsts.TryGetValue(container, out var consts) && consts.ContainsKey(member))
            || (_methods.TryGetValue(container, out var ms) && ms.ContainsKey(member));
    }

    /// <summary>Lower <c>@field(x, "name")</c> — comptime-NAMED field access (road-to-zig-std S5c).
    /// The name is a comptime string, so this is exactly ordinary member access once folded: it
    /// re-enters the shared field-access path rather than duplicating it, so a pointer receiver
    /// auto-derefs and the field type comes from the same aggregate table as <c>x.name</c>.</summary>
    private CExpr LowerFieldBuiltin(IReadOnlyList<Item> bargs)
    {
        if (bargs.Count != 2)
        {
            throw new IrUnsupportedException($"zig `@field` expects (value, name); got {bargs.Count} argument(s)");
        }
        if (bargs[1].Content is not Zig.StrLit lit)
        {
            throw new IrUnsupportedException("zig `@field`: the field name must be a comptime string literal");
        }
        var fieldName = UnquoteStringLiteral(Tok(lit.Arg0));
        var receiver = LowerExpr(bargs[0]);
        var isPtr = receiver.Type.Unqualified is CType.Pointer;
        var fieldType = _ir.StructFieldType(receiver.Type, fieldName)
            ?? throw new IrUnsupportedException(
                $"zig `@field`: {receiver.Type.Describe()} has no field '{fieldName}'");
        return new Member(receiver, fieldName, isPtr) { Type = fieldType, IsLValue = true };
    }

    /// <summary>Resolve a comptime index into a member list, bounds-checked. The index must be
    /// comptime-known: a member list has no runtime representation, so a runtime index could not be
    /// served at all, and saying so beats lowering something that cannot work.</summary>
    private int ComptimeListIndex(ZigComptimeList list, Item indexItem)
    {
        if (EvalComptimeValue(indexItem) is not LitInt { Value: { } n })
        {
            throw new IrUnsupportedException(
                $"zig `{list.Label}[i]`: the index must be comptime-known (a literal or a comptime `const`) — a member "
                + "list is a comptime value with no runtime representation");
        }
        if (n < 0 || n >= list.Count)
        {
            throw new IrUnsupportedException(
                $"zig `{list.Label}[{n}]`: index out of bounds — the list has {list.Count} element(s)");
        }
        return (int)n;
    }
}
