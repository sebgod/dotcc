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
/// dotcc's own registries (<see cref="IrModule.StructFieldsOf"/> for a struct/union's declared
/// fields in order, <see cref="IrModule.Enums"/> for an enum's members in order), never by
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
        public IReadOnlyList<ZigFieldAttr>? Attrs { get; init; }

        public int Count => Strings?.Count ?? Types?.Count ?? Ints?.Count ?? Attrs?.Count ?? 0;
    }

    /// <summary>One <c>std.builtin.Type.Struct.FieldAttributes</c> of a <c>field_attrs</c> list (task #108): the field's
    /// explicit alignment (null when unspelled, as zig's <c>?usize</c>), and whether it has a default value. dotcc has
    /// no <c>comptime</c> fields, so <c>.@"comptime"</c> is false.</summary>
    private sealed record ZigFieldAttr(long? Align, bool HasDefault);

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

    /// <summary>Enum names declared NON-EXHAUSTIVE (a trailing <c>_</c>), for <c>@typeInfo(E).@"enum".mode</c>.</summary>
    private readonly HashSet<string> _nonExhaustiveEnums = new(System.StringComparer.Ordinal);

    /// <summary>The declared FIELDS of a struct/union type in declaration order, or null when the
    /// type names no registered aggregate. Order is load-bearing: <c>field_names</c> and
    /// <c>field_types</c> are index-parallel, and zig guarantees declaration order.</summary>
    private IReadOnlyList<StructField>? FieldsOfAggregate(CType type)
        => type.Unqualified is CType.Named n ? _ir.StructFieldsOf(n.Name) : null;

    /// <summary>The MEMBERS of an enum type in declaration order, or null when the type is not a
    /// registered enum. Read off <see cref="IrModule.Enums"/> — an ordered list, unlike the
    /// name-keyed member-symbol map, which is exactly why this reads the IR registry.</summary>
    private IReadOnlyList<EnumMember>? MembersOfEnum(CType type)
        => type.Unqualified is CType.Enum e
            ? _ir.Enums.FirstOrDefault(d => d.Name == e.Name)?.Members
            : null;

    /// <summary>A tuple's field names, its positions spelled as decimal strings, as zig names them.</summary>
    private static IReadOnlyList<string> TupleFieldNames(CType.Tuple t)
        => Enumerable.Range(0, t.Elements.Count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();

    /// <summary>A member name list as a VALUE of zig's <c>[]const [:0]const u8</c>: each name a string literal viewed as a
    /// slice, all of them in a pinned, program-lifetime array (the comptime memory the zig slice points into). The
    /// interpreter reads it back as a comptime slice of strings, so a call returning it
    /// (<c>std.meta.fieldNames(E)</c>) still folds.</summary>
    private CExpr NameListSlice(IReadOnlyList<string> names)
    {
        var name = new CType.Slice(CType.UChar.WithQuals(TypeQual.Const));
        var element = name.WithQuals(TypeQual.Const);
        var elems = names.Select(n => CoerceToSlice(ZigStringLiteral(n), name)).ToList();
        var pinned = new PinnedArray(element, elems, null) { Type = new CType.Pointer(element) };
        var count = new LitInt(names.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), names.Count) { Type = CType.ULong };
        return new SliceNew(pinned, count, element, true) { Type = new CType.Slice(element) };
    }

    /// <summary>The comptime value of an AGGREGATE argument to a <c>comptime x: anytype</c> parameter, with its type: a tuple
    /// (std.StaticStringMap's <c>initComptime(.{ .{ "one", 1 }, .{ "two", 2 } })</c>, task #100), or a comptime slice of
    /// them (std.meta.stringToEnum's <c>initComptime(kvs)</c>, task #116). Null when the argument is neither or does not
    /// evaluate at compile time (the parameter then binds as an ordinary <c>anytype</c>).</summary>
    private (IrModule.ComptimeValue value, CType type)? ComptimeTupleArg(Item arg)
    {
        using var hoist = EnterThrowawayHoist();
        var lowered = LowerExpr(arg);
        return lowered.Type.Unqualified switch
        {
            CType.Tuple when _ir.EvalComptimeValue(lowered) is IrModule.CtStruct tupleValue => (tupleValue, lowered.Type),
            CType.Slice when _ir.EvalComptimeValue(lowered) is IrModule.CtSlice sliceValue => (sliceValue, lowered.Type),
            _ => null,
        };
    }

    /// <summary>The comptime value of an argument bound to a <c>comptime x: []const T</c> parameter: an integer member
    /// list (<c>@typeInfo(E).@"enum".field_values</c>) as a comptime slice of <c>comptime_int</c>, or any argument the
    /// interpreter evaluates to a slice or array. Null when it is neither.</summary>
    private IrModule.ComptimeValue? ComptimeSliceArg(Item arg, CType.Slice sliceType)
    {
        if (TryFoldTypeInfoList(arg, out var list) || arg.Content is Zig.Ident id && _typeInfoLists.TryGetValue(Tok(id.Arg0), out list))
        {
            if (list.Ints is not { } ints)
            {
                throw new IrUnsupportedException(
                    $"zig `{list.Label}` as a comptime slice argument: only an integer member list (`field_values`) is modeled");
            }
            var element = sliceType.Element.Unqualified;
            var elems = ints.Select(v => (IrModule.ComptimeValue)new IrModule.CtInt(v, element)).ToArray();
            var backing = new IrModule.CtArray(elems, element, new CType.Array(element, elems.Length));
            return new IrModule.CtSlice(backing, 0, elems.Length, sliceType);
        }
        using var hoist = EnterThrowawayHoist();
        var value = _ir.EvalComptimeValue(LowerExprSink(arg, sliceType));
        return value switch
        {
            IrModule.CtSlice => value,
            IrModule.CtArray a => new IrModule.CtSlice(a, 0, a.Elems.Length, sliceType),
            _ => null,
        };
    }

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
        // A list-valued const of the container in scope or one enclosing it (std.MultiArrayList's
        // `const field_names = @typeInfo(Elem).@"struct".field_names;`, walked by `inline for` in a method of its nested
        // `Slice`, task #108): the const's own right-hand side, read with the method's seeds live.
        if (expr.Content is Zig.Ident cid && _symbols.Resolve(Tok(cid.Arg0)) is null)
        {
            for (var c = _currentContainer; c is not null; c = _containerParents.GetValueOrDefault(c))
            {
                if (_containerConsts.TryGetValue(c, out var consts) && consts.TryGetValue(Tok(cid.Arg0), out var decl))
                {
                    return decl.Item1 is null && decl.Item2 != expr && TryFoldTypeInfoList(decl.Item2, out list);
                }
            }
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
                // A TUPLE (`@TypeOf(.{ 42, "x" })`, the args of every std format call) is a struct whose
                // fields are named by position: "0", "1", ... (std.Io.Writer.print reads them).
                if (info.Type.Unqualified is CType.Tuple nt)
                {
                    list = new ZigComptimeList { Label = field, Strings = TupleFieldNames(nt) };
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
                if (info.Type.Unqualified is CType.Tuple tt)
                {
                    list = new ZigComptimeList { Label = field, Types = tt.Elements };
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
                if (info.Type.Unqualified is CType.Named { Name: var attrStruct } && FieldsOfAggregate(info.Type) is { } af)
                {
                    list = new ZigComptimeList { Label = field, Attrs = af.Select(x => FieldAttrOf(attrStruct, x.Name)).ToList() };
                    return true;
                }
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.field_attrs`: only a registered struct / union has "
                    + "field attributes");
        }
        return false;
    }

    /// <summary>The <c>field_attrs</c> entry of <paramref name="structName"/>'s field <paramref name="fieldName"/> (task
    /// #108): its spelled <c>align(N)</c>, evaluated in the module that declared it, and whether it has a default. A field
    /// with no recorded attributes (a reified struct's) has neither.</summary>
    private ZigFieldAttr FieldAttrOf(string structName, string fieldName)
    {
        if (!_shared.StructFieldAttrs.TryGetValue((structName, fieldName), out var a)) { return new ZigFieldAttr(null, false); }
        long? align = null;
        if (a.Align is { } alignItem)
        {
            align = a.Owner.ComptimeIntValue(a.Owner.LowerExpr(alignItem))
                ?? throw new IrUnsupportedException(
                    $"the alignment of '{structName}.{fieldName}' is not a comptime-known integer");
        }
        return new ZigFieldAttr(align, a.HasDefault);
    }

    /// <summary>A member of one <c>field_attrs</c> entry, folded while lowering (task #108): <c>.@"align"</c> is a
    /// <c>?usize</c> (null when unspelled), <c>.@"comptime"</c> false. <c>.default_value_ptr</c> is null for a field
    /// with no default; a defaulted one's pointer to its comptime value is not modeled, a loud cut.</summary>
    private static CExpr FieldAttrMember(ZigFieldAttr attr, string member)
    {
        var optUsize = new CType.Optional(CType.ULong);
        return member switch
        {
            "align" => attr.Align is { } a
                ? new Cast(optUsize, new LitInt(a.ToString(System.Globalization.CultureInfo.InvariantCulture), a) { Type = CType.ULong }) { Type = optUsize }
                : new DefaultLit { Type = optUsize },
            "comptime" => new LitBool(false) { Type = CType.Bool },
            "default_value_ptr" when !attr.HasDefault => new DefaultLit { Type = new CType.Pointer(CType.Void) },
            "default_value_ptr" => throw new IrUnsupportedException(
                "a defaulted field's `field_attrs[i].default_value_ptr` (a pointer to its comptime default) is not modeled yet"),
            _ => throw new IrUnsupportedException($"std.builtin.Type.Struct.FieldAttributes has no member '{member}'"),
        };
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
        // `<attrs>[i].@"align"` at a comptime index, and `f_attrs.@"align"` of a capture bound to one (task #108).
        if (expr.Content is Zig.Field af)
        {
            if (af.Arg0.Content is Zig.Index aix && TryFoldTypeInfoList(aix.Arg0, out var attrList) && attrList.Attrs is { } attrs)
            {
                value = FieldAttrMember(attrs[ComptimeListIndex(attrList, aix.Arg2)], Tok(af.Arg2));
                return true;
            }
            if (af.Arg0.Content is Zig.Ident aid && _symbols.Resolve(Tok(aid.Arg0)) is null
                && _comptimeAttrs.TryGetValue(Tok(aid.Arg0), out var boundAttr))
            {
                value = FieldAttrMember(boundAttr, Tok(af.Arg2));
                return true;
            }
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

    /// <summary>The width zig infers for an untyped enum's tag: the bits of its largest member value (members count up
    /// from 0), 0 for a single member.</summary>
    private int InferredTagBits(CType.Enum en)
    {
        var members = MembersOfEnum(en)?.Count ?? 0;
        return members <= 1 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)(members - 1));
    }

    /// <summary>The standard unsigned carrier for an inferred tag of <paramref name="bits"/> bits.</summary>
    private static CType InferredTagCarrier(int bits) => bits switch
    {
        <= 8 => CType.UChar,
        <= 16 => CType.UShort,
        <= 32 => CType.UInt,
        _ => CType.ULong,
    };

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
                // A SPELLED tag (`enum(u8) {…}`) is the enum's own underlying type. An untyped enum's is zig's INFERRED
                // one (std.enums.EnumIndexer's `@as(@typeInfo(E).@"enum".tag_type, …)`): the smallest unsigned int that
                // holds its largest member, `u2` for four, in the smallest standard carrier, with that declared width
                // riding along (InferredTagBits) so `@bitSizeOf` answers zig's 2, not the carrier's 8.
                type = _enumsWithSpelledTag.Contains(en.Name) ? en.Underlying : InferredTagCarrier(InferredTagBits(en));
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
        // A string literal, or a name bound to one by an `inline for` capture (road-to-zig-std S6) —
        // `inline for (field_names) |f| { if (@hasField(T, f)) … }` is the shape this serves.
        if (ComptimeStringArg(args[1]) is not { } member)
        {
            throw new IrUnsupportedException(
                $"zig `{name}`: the member name must be a comptime string — a literal, or an `inline for` capture "
                + "over a member list");
        }
        // `@hasDecl(root, "std_options")` (std.zig's `options`): a MODULE operand asks about its top-level
        // declarations. dotcc's synthetic `root` is empty, so std takes its defaults, as for a program that
        // declares no overrides.
        if (name == "@hasDecl" && args[0].Content is Zig.Ident or Zig.Field
            && _typeAliases.GetValueOrDefault(args[0].Content is Zig.Ident mi ? Tok(mi.Arg0) : "") is null
            && ResolveModulePath(args[0]) is { Lowering: { } declModule })
        {
            return new LitBool(declModule.DeclaresTopLevel(member)) { Type = CType.Bool };
        }
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
        // As for `@hasField`: a literal, or an `inline for` capture over `field_names` — which is
        // exactly the `inline for (field_names) |f| … @field(x, f) …` idiom S6 exists to serve.
        if (ComptimeStringArg(bargs[1]) is not { } fieldName)
        {
            throw new IrUnsupportedException(
                "zig `@field`: the field name must be a comptime string — a literal, or an `inline for` capture "
                + "over a member list");
        }
        // `@field(T, name)` with `T` an enum TYPE (std.meta.stringToEnum's `.{ name, @field(T, name) }`, task #116): the member
        // it names, as `T.name` is. `T` may be a type const of an enclosing container (std.MultiArrayList.Slice.swap's
        // `@field(Field, field_name)`, `Field` declared on the instance, task #108).
        if (bargs[0].Content is Zig.Ident typeIdent && _symbols.Resolve(Tok(typeIdent.Arg0)) is null
            && TryLookupContainerType(Tok(typeIdent.Arg0), out var receiverType) && receiverType.Unqualified is CType.Enum receiverEnum)
        {
            return ResolveEnumLit(fieldName, receiverEnum);
        }
        var receiver = LowerExpr(bargs[0]);
        // A TUPLE's fields are named by position (`"0"`, `"1"`: what `field_names` lists for `.{ a, b }`), so
        // `@field(args, field_names[i])` in std.Io.Writer.print is its i-th element.
        if (receiver.Type.Unqualified is CType.Tuple tuple
            && int.TryParse(fieldName, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var position)
            && position < tuple.Elements.Count)
        {
            return new TupleIndex(receiver, position, tuple.Elements[position]) { Type = tuple.Elements[position] };
        }
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
        // A literal or comptime binding, else any index the const-folder settles (a `const` whose
        // initializer folded, std.Io.Writer.print's `arg_to_print`).
        long? folded = EvalComptimeValue(indexItem) is LitInt { Value: { } lit } ? lit : null;
        if (folded is null)
        {
            using (EnterThrowawayHoist()) { folded = _ir.ConstEval(LowerExpr(indexItem)); }
        }
        if (folded is not { } n)
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
