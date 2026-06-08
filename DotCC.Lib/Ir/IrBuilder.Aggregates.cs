#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Item = global::LALR.CC.LexicalGrammar.Item;

namespace DotCC.Ir;

/// <summary>
/// Aggregate-initializer lowering for the typed IR: brace initializers, C99
/// designated initializers (<c>.field =</c> / <c>[i] =</c>), nested-brace and
/// multi-dimensional arrays, struct-element arrays, compound literals, and the
/// C23 empty initializer. Everything resolves against the TARGET <see cref="CType"/>
/// (the binder knows the field/element types), so a value's lowering — including
/// a bare function name decaying to <c>&amp;fn</c> — falls out of the typed nodes
/// rather than any text rewriting.
/// </summary>
internal sealed partial class IrBuilder
{
    // ---- structured brace-initializer tree -------------------------------
    // A brace initializer parses into a small structured tree so designators and
    // nesting survive into the type-directed interpretation below. Leaf values are
    // built to CExpr eagerly (BuildExpr needs no target type); the target type is
    // applied when the tree is interpreted against a struct/array.

    private abstract record Init;
    private sealed record InitVal(CExpr Value) : Init;
    private sealed record InitGroup(IReadOnlyList<Init> Items) : Init;
    private sealed record InitAt(int Index, CExpr Value) : Init;

    /// <summary>Parse an <c>InitList</c> (its element list, with the optional
    /// trailing comma) into the structured init tree.</summary>
    private List<Init> ParseInitList(Item initList)
    {
        var items = new List<Init>();
        void Walk(Item n)
        {
            switch (n.Content)
            {
                case C.InitListCons c: Walk(c.Arg0); items.Add(ParseInitElem(c.Arg2)); break;
                case C.InitListTrail t: Walk(t.Arg0); break;     // trailing comma — no element
                case C.InitListOne o: items.Add(ParseInitElem(o.Arg0)); break;
                default: items.Add(ParseInitElem(n)); break;
            }
        }
        Walk(initList);
        return items;
    }

    private Init ParseInitElem(Item it) => it.Content switch
    {
        C.InitElemExpr e => new InitVal(BuildExpr(e.Arg0)),
        C.InitElemNest nest => new InitGroup(ParseInitList(nest.Arg1)),
        C.InitElemDesignated d => Gated(1999, "array designators", it, new InitAt(
            ConstEval(BuildExpr(d.Arg1)) is { } ix && ix >= 0 ? (int)ix
                : throw new IrUnsupportedException("array designator index must be a constant non-negative integer"),
            BuildExpr(d.Arg4))),
        _ => new InitVal(BuildExpr(it)),
    };

    // ---- struct / union aggregates ---------------------------------------

    /// <summary>Build a positional struct/union aggregate initializer — zips the
    /// brace elements onto the fields in declaration order, recursing into a nested
    /// brace over a struct/union-typed field. Trailing fields the list doesn't
    /// reach are omitted (C# zero-fills them — C's partial-init rule). This is the
    /// one place the legacy emitter's positional <c>BuildAggregateInit</c> and the
    /// struct-array element builder converge.</summary>
    private StructInit BuildStructPositional(CType type, IReadOnlyList<Init> items)
    {
        var fields = StructFieldsOf(type);
        var members = new List<FieldInit>(Math.Min(items.Count, fields.Count));
        for (var i = 0; i < items.Count && i < fields.Count; i++)
        {
            var field = fields[i];
            CExpr value = items[i] switch
            {
                InitVal v => v.Value,
                InitGroup g => BuildStructPositional(field.Type, g.Items), // nested brace → struct/union field
                _ => throw new IrUnsupportedException("array designator inside a positional struct initializer"),
            };
            members.Add(new FieldInit(field.Name, field.Type, value));
        }
        return new StructInit(members) { Type = type };
    }

    /// <summary>Build a C99 designated struct/union initializer
    /// (<c>{ .x = 1, .y = 2 }</c>). The user named the fields, so each member's
    /// field type comes from the struct table (driving the store coercion) and the
    /// order may differ from declaration — C# object initializers allow both, and
    /// omitted fields take their zero default.</summary>
    private StructInit BuildStructDesignated(CType type, Item memberList)
    {
        var fields = StructFieldsOf(type);
        var members = new List<FieldInit>();
        foreach (var (field, valueItem) in ParseMemberInits(memberList))
        {
            members.Add(new FieldInit(field, FieldTypeOf(fields, field), BuildExpr(valueItem)));
        }
        return new StructInit(members) { Type = type };
    }

    /// <summary>The struct/union fields named by <paramref name="type"/>, or throw
    /// if it isn't a known aggregate.</summary>
    private List<StructField> StructFieldsOf(CType type)
    {
        var canonical = (type.Unqualified as CType.Named)?.Name
            ?? throw new IrUnsupportedException("aggregate initializer for a non-struct type");
        return _structFields.TryGetValue(canonical, out var fields) ? fields
            : throw new IrUnsupportedException($"aggregate initializer for unknown struct/union '{canonical}'");
    }

    private static CType FieldTypeOf(List<StructField> fields, string name)
    {
        foreach (var f in fields) { if (f.Name == name) { return f.Type; } }
        return CType.Int;   // unknown field — let Roslyn surface the real error
    }

    /// <summary>Collect a <c>MemberInitList</c>'s <c>.field = value</c> items, in
    /// source order.</summary>
    private List<(string field, Item value)> ParseMemberInits(Item memberList)
    {
        var outp = new List<(string, Item)>();
        void Add(Item mi)
        {
            if (mi.Content is C.MemberInit m) { outp.Add((Tok(m.Arg1), m.Arg3)); }
            else { throw new IrUnsupportedException(TypeName(mi.Content)); }
        }
        void Walk(Item n)
        {
            switch (n.Content)
            {
                case C.MemberInitListCons c: Walk(c.Arg0); Add(c.Arg2); break;
                case C.MemberInitListTrail t: Walk(t.Arg0); break;
                case C.MemberInitListOne o: Add(o.Arg0); break;
                default: Add(n); break;
            }
        }
        Walk(memberList);
        return outp;
    }

    // ---- array aggregates ------------------------------------------------

    /// <summary>Interpret a brace initializer against an array TARGET, returning the
    /// dense element list codegen lays into a <c>stackalloc</c>. Dispatches on the
    /// element type and shape: a struct element type maps each top-level group to a
    /// <see cref="StructInit"/>; C99 array designators (<c>[i] =</c>) fill a sparse
    /// 1-D array; constant dimensions flatten a nested/flat scalar initializer with
    /// C's per-dimension zero-fill; an implicit <c>[]</c> takes the values as-is.</summary>
    /// <param name="dims">The constant dimension sizes, or null when implicit
    /// (<c>[]</c>) or non-constant.</param>
    private List<CExpr> BuildArrayElems(CType elem, IReadOnlyList<int>? dims, IReadOnlyList<Init> items)
    {
        var elemName = (elem.Unqualified as CType.Named)?.Name;
        if (elemName is not null && _structFields.ContainsKey(elemName))
        {
            // struct-element array — each top-level item is a `{ … }` group.
            var outp = new List<CExpr>(items.Count);
            foreach (var it in items)
            {
                if (it is not InitGroup g)
                {
                    throw new IrUnsupportedException($"each element of a '{elemName}' array initializer must be a brace group");
                }
                outp.Add(BuildStructPositional(elem, g.Items));
            }
            return outp;
        }
        if (items.Any(i => i is InitAt))
        {
            if (dims is { Count: > 1 }) { throw new IrUnsupportedException("array designators on a multi-dimensional array"); }
            return DesignatedArrayValues(elem, items, dims is { Count: 1 } ? dims[0] : -1);
        }
        if (dims is { Count: > 0 })
        {
            return FlattenScalarArray(elem, items, dims);
        }
        // implicit `[]` scalar array — the values as written.
        return items.Select(it => it is InitVal v ? v.Value
            : throw new IrUnsupportedException("nested brace in an implicitly-sized scalar array")).ToList();
    }

    /// <summary>Build the dense, zero-filled value list for a 1-D scalar array with
    /// C99 array designators: a <c>[i] =</c> moves the cursor to <c>i</c>, an
    /// undesignated value fills the cursor, both advance it (a later write to the
    /// same index wins). <paramref name="declaredSize"/> is the constant size, or
    /// -1 to derive it from the highest index touched (the implicit form).</summary>
    private List<CExpr> DesignatedArrayValues(CType elem, IReadOnlyList<Init> items, int declaredSize)
    {
        var slots = new Dictionary<int, CExpr>();
        int cursor = 0, maxIndex = -1;
        foreach (var it in items)
        {
            switch (it)
            {
                case InitAt d: cursor = d.Index; slots[cursor] = d.Value; break;
                case InitVal v: slots[cursor] = v.Value; break;
                default: throw new IrUnsupportedException("nested brace mixed with array designators");
            }
            if (cursor > maxIndex) { maxIndex = cursor; }
            cursor++;
        }
        var size = declaredSize >= 0 ? declaredSize : maxIndex + 1;
        if (maxIndex >= size) { throw new IrUnsupportedException($"array designator index {maxIndex} is out of bounds for [{size}]"); }
        var outp = new List<CExpr>(size);
        for (var i = 0; i < size; i++) { outp.Add(slots.TryGetValue(i, out var v) ? v : Zero); }
        return outp;
    }

    /// <summary>Flatten a (possibly nested) scalar array initializer against the
    /// constant dimensions, applying C's per-dimension zero-fill, to exactly
    /// product(dims) values. Handles the fully-flat (<c>{1,2,3,4,5,6}</c>) and
    /// fully-nested (<c>{{1,2,3},{4,5,6}}</c>) shapes; an irregular mix fails
    /// loudly rather than miscompile.</summary>
    private List<CExpr> FlattenScalarArray(CType elem, IReadOnlyList<Init> items, IReadOnlyList<int> dims)
    {
        var total = 1;
        foreach (var d in dims) { total *= d; }
        var outp = new List<CExpr>(total);
        if (items.All(i => i is InitVal))
        {
            foreach (var it in items) { outp.Add(((InitVal)it).Value); }
            if (outp.Count > total) { throw new IrUnsupportedException("too many initializers for array"); }
        }
        else
        {
            FlattenNested(items, dims, 0, outp);
        }
        while (outp.Count < total) { outp.Add(Zero); }   // zero-fill the tail
        return outp;
    }

    private void FlattenNested(IReadOnlyList<Init> items, IReadOnlyList<int> dims, int dimIdx, List<CExpr> outp)
    {
        if (items.Count > dims[dimIdx]) { throw new IrUnsupportedException("too many initializers for an array dimension"); }
        if (dimIdx == dims.Count - 1)
        {
            foreach (var it in items)
            {
                if (it is InitVal v) { outp.Add(v.Value); }
                else { throw new IrUnsupportedException("irregular nested array initializer"); }
            }
            for (var k = items.Count; k < dims[dimIdx]; k++) { outp.Add(Zero); }
        }
        else
        {
            var subSize = 1;
            for (var i = dimIdx + 1; i < dims.Count; i++) { subSize *= dims[i]; }
            foreach (var it in items)
            {
                if (it is InitGroup g) { FlattenNested(g.Items, dims, dimIdx + 1, outp); }
                else { throw new IrUnsupportedException("irregular nested array initializer (mixed braces and scalars)"); }
            }
            for (var k = items.Count; k < dims[dimIdx]; k++)
            {
                for (var z = 0; z < subSize; z++) { outp.Add(Zero); }
            }
        }
    }

    /// <summary>The integer constant 0 — the zero-fill element. C# converts the
    /// int literal to any scalar element type in an array initializer.</summary>
    private static LitInt Zero => new("0", 0) { Type = CType.Int };

    /// <summary>Build the nested C array type from outer→inner dimensions
    /// (<c>[2][3]</c> → <c>Array(Array(elem, 3), 2)</c>). The nesting is what lets a
    /// partial subscript yield an inner array (and stride correctly); the storage
    /// and <see cref="CType.CsType"/> still collapse to one flat pointer.</summary>
    private static CType MakeArrayType(CType elem, IReadOnlyList<int> dims)
    {
        var t = elem;
        for (var i = dims.Count - 1; i >= 0; i--) { t = new CType.Array(t, dims[i]); }
        return t;
    }

    /// <summary>A pointer-to-array declaration <c>T (*p)[N]…</c> — a pointer whose
    /// pointee is an array (a row pointer into a 2-D array). Lowered to a flat
    /// pointer that strides by the array's extent; the type carries the nested array
    /// pointee so subscript striding and <c>sizeof</c> resolve.</summary>
    private DeclStmt BuildPtrToArr(Item typeItem, Item nameItem, Item dimsItem, Item? initItem)
    {
        var elem = ResolveType(typeItem);
        var dims = TryConstDims(dimsItem) ?? throw new IrUnsupportedException("pointer-to-array needs constant dimensions");
        var type = new CType.Pointer(MakeArrayType(elem, dims));
        var sym = _symbols.Declare(new Symbol { Name = Tok(nameItem), Kind = SymKind.Var, Type = type, Storage = Storage.Auto });
        return new DeclStmt(new[] { new LocalDecl(sym, initItem is { } ii ? BuildExpr(ii) : null) });
    }

    /// <summary>The constant dimension sizes of an <c>ArrDims</c> node, or null when
    /// any dimension isn't an integer constant expression (a VLA-ish extent).</summary>
    private List<int>? TryConstDims(Item arrDims)
    {
        var outp = new List<int>();
        foreach (var d in BuildArrDims(arrDims))
        {
            if (ConstEval(d) is { } n) { outp.Add((int)n); }
            else { return null; }
        }
        return outp;
    }

    // ---- compound literals (C99 / C23) -----------------------------------

    /// <summary>A struct/union (or scalar) compound literal <c>(T){ … }</c> — an
    /// unnamed object usable in any expression position. A struct lowers to a
    /// <see cref="StructInit"/> (<c>new T { … }</c>); a scalar/pointer/enum to a
    /// single-value cast (<c>(T)(v)</c>).</summary>
    private CExpr BuildCompoundLit(Item typeItem, Item initListItem)
    {
        var type = ResolveType(typeItem);
        if ((type.Unqualified as CType.Named)?.Name is { } canonical && _structFields.ContainsKey(canonical))
        {
            return BuildStructPositional(type, ParseInitList(initListItem));
        }
        var items = ParseInitList(initListItem);
        if (items is [InitVal one]) { return new Cast(type, one.Value) { Type = type }; }
        throw new IrUnsupportedException($"compound literal of non-aggregate type '{type.CsType}' needs exactly one value");
    }

    private CExpr BuildCompoundLitDesignated(Item typeItem, Item memberList) =>
        BuildStructDesignated(ResolveType(typeItem), memberList);

    private CExpr BuildCompoundLitEmpty(Item typeItem) =>
        new DefaultLit { Type = ResolveType(typeItem) };

    /// <summary>An array compound literal <c>(T[]){…}</c> / <c>(T[N]){…}</c> — a
    /// <see cref="StackArray"/> value (codegen: a <c>stackalloc</c>, valid in
    /// initializer position).</summary>
    private CExpr BuildArrayCompoundLit(Item elemTypeItem, Item? dimsItem, Item initListItem)
    {
        var elem = ResolveType(elemTypeItem);
        var dims = dimsItem is { } di ? TryConstDims(di) : null;
        var elems = BuildArrayElems(elem, dims, ParseInitList(initListItem));
        return new StackArray(elem, elems) { Type = new CType.Array(elem, elems.Count) };
    }

    // ---- file-scope / static-local arrays --------------------------------
    // A C file-scope array (and a block-scope `static` array, which shares its
    // static storage duration) persists for the program lifetime, so it can't be a
    // block `stackalloc`. Both lower to a pinned global field (a PinnedArray init);
    // a static local additionally gets a program-unique mangled name + an alias
    // symbol so the function body's references resolve to that field.

    /// <summary>Build a file-scope array <see cref="GlobalVar"/> (a pinned backing
    /// store). When <paramref name="csName"/> is non-null this is a static local —
    /// the field takes that mangled name and an alias symbol is registered so
    /// in-function uses resolve to it; otherwise it's a file-scope name.</summary>
    private void BuildGlobalArr(Item typeItem, Item nameItem, Item? dimsItem, Item? initItem, string? csName)
    {
        var elem = ResolveType(typeItem);
        var name = Tok(nameItem);
        var dims = dimsItem is { } di ? TryConstDims(di) : null;

        CType arrType;
        CExpr init;
        if (initItem is { } ii)
        {
            var elems = BuildArrayElems(elem, dims, ParseInitList(ii));
            arrType = dims is { Count: >= 1 } ? MakeArrayType(elem, dims) : new CType.Array(elem, elems.Count);
            init = new PinnedArray(elem, elems, null) { Type = new CType.Pointer(elem) };
        }
        else if (dims is { Count: >= 1 })
        {
            var total = 1;
            foreach (var d in dims) { total *= d; }
            arrType = MakeArrayType(elem, dims);
            init = new PinnedArray(elem, null, new LitInt(total.ToString(System.Globalization.CultureInfo.InvariantCulture), total) { Type = CType.Int }) { Type = new CType.Pointer(elem) };
        }
        else
        {
            throw new IrUnsupportedException($"file-scope array '{name}' needs a constant size or an initializer");
        }

        AddGlobalArray(name, arrType, init, csName);
    }

    /// <summary>Register a global-array symbol and its <see cref="GlobalVar"/>. A
    /// non-null <paramref name="csName"/> marks a static local (mangled field name +
    /// alias symbol); otherwise it's a file-scope name.</summary>
    private void AddGlobalArray(string name, CType arrType, CExpr init, string? csName)
    {
        if (csName is not null)
        {
            var sym = new Symbol { Name = name, Kind = SymKind.Var, Type = arrType, Storage = Storage.Static, IsGlobal = true, CsName = csName };
            Globals.Add(new GlobalVar(sym, init));
            _symbols.DeclareAlias(sym);
        }
        else
        {
            var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = arrType, Storage = Storage.Static, IsGlobal = true });
            Globals.Add(new GlobalVar(sym, init));
        }
    }

    /// <summary>A file-scope / static-local char array initialized from a string
    /// literal (<c>char tag[] = "…"</c>) — a pinned byte array of the decoded bytes
    /// plus the NUL, zero-padded to an explicit size (or truncated, C's rule).</summary>
    private void BuildGlobalCharArr(Item typeItem, Item nameItem, Item strSeqItem, Item? dimsItem, string? csName)
    {
        var elem = ResolveType(typeItem);
        var bytes = DotCC.EmitHelpers.StringByteValues(CollectStrSegments(strSeqItem));
        bytes.Add(0);   // NUL
        var dims = dimsItem is { } di ? TryConstDims(di) : null;
        var total = dims is { Count: >= 1 } ? dims.Aggregate(1, (a, b) => a * b) : bytes.Count;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var elems = new List<CExpr>(total);
        for (var i = 0; i < total; i++)
        {
            var v = i < bytes.Count ? bytes[i] : 0;   // zero-pad beyond the string
            elems.Add(new LitInt(v.ToString(inv), v) { Type = CType.Int });
        }
        AddGlobalArray(Tok(nameItem), new CType.Array(elem, total),
            new PinnedArray(elem, elems, null) { Type = new CType.Pointer(elem) }, csName);
    }

    /// <summary>An <c>extern T a[N];</c> / <c>extern T a[];</c> declaration — storage
    /// lives in another TU (or a later same-TU definition), so emit no field; just
    /// register the name's type so same-TU references resolve (a sized extent keeps
    /// the array type for <c>sizeof</c>; an incomplete one decays to a pointer).</summary>
    private void BuildExternArr(Item typeItem, Item nameItem, Item? dimsItem)
    {
        var elem = ResolveType(typeItem);
        var dims = dimsItem is { } di ? TryConstDims(di) : null;
        var type = dims is { Count: >= 1 } ? MakeArrayType(elem, dims) : new CType.Pointer(elem);
        _symbols.Declare(new Symbol { Name = Tok(nameItem), Kind = SymKind.Var, Type = type, Storage = Storage.Extern, IsGlobal = true });
    }

    /// <summary>A block-scope <c>static T a[…]</c> — a pinned global field under a
    /// program-unique mangled name, with the statement itself emitting nothing.</summary>
    private CStmt BuildStaticLocalArr(Item typeItem, Item nameItem, Item? dimsItem, Item? initItem)
    {
        var csName = $"{DotCC.EmitHelpers.Id(Tok(nameItem))}__s{_staticLocalSeq++}";
        BuildGlobalArr(typeItem, nameItem, dimsItem, initItem, csName);
        return new DeclStmt(System.Array.Empty<LocalDecl>());
    }

    /// <summary>A block-scope <c>static char a[] = "…"</c> — a pinned global char
    /// array under a mangled name (the statement emits nothing).</summary>
    private CStmt BuildStaticLocalCharArr(Item typeItem, Item nameItem, Item strSeqItem, Item? dimsItem)
    {
        var csName = $"{DotCC.EmitHelpers.Id(Tok(nameItem))}__s{_staticLocalSeq++}";
        BuildGlobalCharArr(typeItem, nameItem, strSeqItem, dimsItem, csName);
        return new DeclStmt(System.Array.Empty<LocalDecl>());
    }
}
