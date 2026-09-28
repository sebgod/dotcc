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
    /// <summary>A member designator <c>.field = value</c> (value: an InitVal or InitGroup).</summary>
    private sealed record InitMember(string Field, Init Value) : Init;

    /// <summary>Parse an <c>InitList</c> (its element list, with the optional
    /// trailing comma) into the structured init tree.</summary>
    private List<Init> ParseInitList(Item initList)
    {
        var items = new List<Init>();
        // A C23 #embed element expands IN PLACE to its file bytes as integer
        // constants — so `{ #embed "f" }` fills a char array, `{ 1, #embed "f", 2 }`
        // splices into a mixed list, and a non-char element type takes one int per
        // byte. This single chokepoint serves every initializer-list shape; the
        // const→RVA fast path then recognises a const char[] of constant bytes in
        // the backend (string- and embed-init converge on the same node).
        void AddElem(Init e)
        {
            if (e is InitVal { Value: EmbedData ed })
            {
                GuardEmbedSize(ed.Bytes.Count);
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var b in ed.Bytes)
                {
                    items.Add(new InitVal(new LitInt(b.ToString(inv), b) { Type = CType.Int }));
                }
            }
            else { items.Add(e); }
        }
        void Walk(Item n)
        {
            switch (n.Content)
            {
                case C.InitListCons c: Walk(c.Arg0); AddElem(ParseInitElem(c.Arg2)); break;
                case C.InitListTrail t: Walk(t.Arg0); break;     // trailing comma — no element
                case C.InitListOne o: AddElem(ParseInitElem(o.Arg0)); break;
                default: AddElem(ParseInitElem(n)); break;
            }
        }
        Walk(initList);
        return items;
    }

    /// <summary>Upper bound on a single <c>#embed</c>'s byte count. The bytes
    /// ultimately materialise as a <c>new byte[]{…}</c> source-text literal, so
    /// the ceiling is source-emit size, not runtime cost (the RVA blob is free at
    /// runtime). Exceeding it is a loud error, never a silent truncation.</summary>
    private const int MaxEmbedBytes = 4 * 1024 * 1024;

    private static void GuardEmbedSize(int count)
    {
        if (count > MaxEmbedBytes)
        {
            throw new IrUnsupportedException(
                $"#embed payload is {count} bytes, exceeding dotcc's {MaxEmbedBytes}-byte source-emit limit");
        }
    }

    private Init ParseInitElem(Item it) => it.Content switch
    {
        C.InitElemExpr e => new InitVal(BuildExpr(e.Arg0)),
        C.InitElemNest nest => new InitGroup(ParseInitList(nest.Arg1)),
        C.InitElemDesignated d => Gated(1999, "array designators", it, new InitAt(
            ConstEval(BuildExpr(d.Arg1)) is { } ix && ix >= 0 ? (int)ix
                : throw new IrUnsupportedException("array designator index must be a constant non-negative integer"),
            BuildExpr(d.Arg4))),
        C.InitElemMember m => Gated(1999, "designated initializers", it,
            new InitMember(Tok(m.Arg1), new InitVal(BuildExpr(m.Arg3)))),
        C.InitElemMemberNest m => Gated(1999, "designated initializers", it,
            new InitMember(Tok(m.Arg1), new InitGroup(ParseInitList(m.Arg4)))),
        _ => new InitVal(BuildExpr(it)),
    };

    // ---- struct / union aggregates ---------------------------------------

    /// <summary>Build a positional struct/union aggregate initializer: the brace
    /// elements land on the fields in declaration order. Trailing fields the list
    /// doesn't reach are omitted (C# zero-fills them, C's partial-init rule). This is
    /// the one place the legacy emitter's positional <c>BuildAggregateInit</c> and the
    /// struct-array element builder converge.</summary>
    private StructInit BuildStructPositional(CType type, IReadOnlyList<Init> items)
    {
        var cursor = 0;
        return BuildStructFrom(type, items, ref cursor, braced: true);
    }

    /// <summary>Consume <paramref name="items"/> from <paramref name="cursor"/> onto the
    /// fields of <paramref name="type"/>. Each field takes a braced group or (by C's brace
    /// elision, C11 §6.7.9p20) as many of the following items as it needs: a nested
    /// struct its fields' worth, an array member its element count. A union initializes
    /// through its FIRST member only (§6.7.9p17) unless a designator names another.
    /// A member designator <c>.f = v</c> (only in the <paramref name="braced"/> list it
    /// belongs to; inside an elided sub-aggregate it ends the elision and applies to the
    /// enclosing braces, §6.7.9p17) initializes <c>f</c>, and the next positional element
    /// continues with the member after it; a later initializer for the same member
    /// overrides an earlier one.</summary>
    private StructInit BuildStructFrom(CType type, IReadOnlyList<Init> items, ref int cursor, bool braced)
    {
        // Anonymous bit-fields (padding) take no initializer in C — drop them so the
        // positional values land on the accessible members in declaration order.
        var fields = StructFieldsOf(type).Where(f => !f.IsAnonBitField).ToList();
        var positionalLimit = IsUnionType(type) ? Math.Min(1, fields.Count) : fields.Count;
        var members = new List<FieldInit>(fields.Count);
        void Set(StructField f, CExpr value)
        {
            members.RemoveAll(m => m.Name == f.Name);
            members.Add(new FieldInit(f.Name, f.Type, value));
        }
        var next = 0;
        while (cursor < items.Count)
        {
            if (items[cursor] is InitMember im)
            {
                if (!braced) { break; }
                var at = fields.FindIndex(f => f.Name == im.Field);
                if (at < 0)
                {
                    throw new IrUnsupportedException($"designator '.{im.Field}' names no member of '{(type.Unqualified as CType.Named)?.Name}'");
                }
                var one = 0;
                Set(fields[at], BuildMemberValue(fields[at].Type, [im.Value], ref one));
                cursor++;
                next = at + 1;
                continue;
            }
            if (next >= positionalLimit) { break; }
            Set(fields[next], BuildMemberValue(fields[next].Type, items, ref cursor));
            next++;
        }
        return new StructInit(members) { Type = type };
    }

    /// <summary>The initializer for one aggregate member (or array element) of type
    /// <paramref name="ft"/>, consuming one braced item or (brace elision) the run of
    /// items it needs.</summary>
    private CExpr BuildMemberValue(CType ft, IReadOnlyList<Init> items, ref int cursor)
    {
        var item = items[cursor];
        if (item is InitAt or InitMember)
        {
            throw new IrUnsupportedException("a designator where a member's value was expected");
        }
        if (ft.Unqualified is CType.Array arr)
        {
            // `char name[8] = "…"` inside a struct: the string's bytes, braced or not.
            if (item is InitVal sv && StringArrayValue(arr, sv.Value) is { } str) { cursor++; return str; }
            if (item is InitGroup { Items: [InitVal bsv] } && StringArrayValue(arr, bsv.Value) is { } bstr) { cursor++; return bstr; }
            if (item is InitGroup g) { cursor++; return BuildArrayValue(arr, g.Items); }
            return ElidedArrayValue(arr, items, ref cursor);
        }
        if (IsAggregateType(ft))
        {
            if (item is InitGroup g) { cursor++; return BuildStructPositional(ft, g.Items); }
            // A whole-struct value (`{ other, 3 }` where `other` is itself a struct).
            if (item is InitVal v && v.Value.Type.Unqualified is CType.Named vn
                && ft.Unqualified is CType.Named fn && vn.Name == fn.Name)
            {
                cursor++;
                return v.Value;
            }
            return BuildStructFrom(ft, items, ref cursor, braced: false);
        }
        cursor++;
        return item switch
        {
            InitVal v => v.Value,
            InitGroup { Items: [InitVal inner, ..] } => inner.Value,   // `{ 5 }`, a braced scalar
            _ => throw new IrUnsupportedException("an empty or nested brace around a scalar initializer"),
        };
    }

    /// <summary>A braced initializer for an array-typed member: designators, nested
    /// braces and multi-dimensional flattening go through the ordinary array
    /// interpreter (<see cref="BuildArrayElems"/>).</summary>
    private ArrayValue BuildArrayValue(CType.Array arr, IReadOnlyList<Init> items)
    {
        var dims = ConstDimsOf(arr);
        var elems = BuildArrayElems(arr.FlatElement, dims, items);
        if (elems.Count > dims.Aggregate(1, (a, b) => a * b))
        {
            throw new IrUnsupportedException("too many initializers for an array member");
        }
        return new ArrayValue(arr.FlatElement, elems) { Type = arr };
    }

    /// <summary>An array member whose braces were elided: it takes the following items,
    /// one flat element each (a struct element itself elides), up to its extent.</summary>
    private ArrayValue ElidedArrayValue(CType.Array arr, IReadOnlyList<Init> items, ref int cursor)
    {
        var total = ConstDimsOf(arr).Aggregate(1, (a, b) => a * b);
        var elems = new List<CExpr>();
        while (elems.Count < total && cursor < items.Count && items[cursor] is not (InitMember or InitAt))
        {
            elems.Add(BuildMemberValue(arr.FlatElement, items, ref cursor));
        }
        return new ArrayValue(arr.FlatElement, elems) { Type = arr };
    }

    /// <summary>A string literal initializing a 1-D character array member: its code
    /// units plus the NUL, truncated to the extent (an exact fit drops the NUL, C's
    /// rule). Null when <paramref name="value"/> isn't a string literal matching the
    /// element width.</summary>
    private static ArrayValue? StringArrayValue(CType.Array arr, CExpr value)
    {
        if (arr.Element is CType.Array || arr.Element.Unqualified is not CType.Prim p) { return null; }
        List<int>? units = value switch
        {
            LitStr s when p.Bytes == 1 => DotCC.EmitHelpers.StringByteValues(s.Segments),
            LitU16Str s when p.Bytes == 2 => DotCC.EmitHelpers.StringU16Values(s.Segments),
            LitU32Str s when p.Bytes == 4 => DotCC.EmitHelpers.StringU32Values(s.Segments),
            _ => null,
        };
        if (units is null) { return null; }
        units.Add(0);
        var extent = arr.Count ?? units.Count;
        var elems = new List<CExpr>(extent);
        for (var i = 0; i < units.Count && i < extent; i++)
        {
            elems.Add(new LitInt(units[i].ToString(System.Globalization.CultureInfo.InvariantCulture), units[i]) { Type = CType.Int });
        }
        return new ArrayValue(arr.Element, elems) { Type = arr };
    }

    /// <summary>The constant extents of a (possibly nested) array type, outer → inner.</summary>
    private static List<int> ConstDimsOf(CType.Array arr)
    {
        var dims = new List<int>();
        for (CType t = arr; t is CType.Array a; t = a.Element)
        {
            dims.Add(a.Count ?? throw new IrUnsupportedException("initializer for an array member of unknown size"));
        }
        return dims;
    }

    private bool IsAggregateType(CType t) => t.Unqualified is CType.Named n && _structFields.ContainsKey(n.Name);

    private bool IsUnionType(CType t) => t.Unqualified is CType.Named n && _structIsUnion.GetValueOrDefault(n.Name);

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
    /// and the backend's flat-pointer projection still collapse to one pointer.</summary>
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
        throw new IrUnsupportedException($"compound literal of non-aggregate type '{type.Describe()}' needs exactly one value");
    }

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
        => BuildGlobalArr(ResolveType(typeItem), nameItem, dimsItem, initItem, csName);

    /// <summary><see cref="BuildGlobalArr(Item, Item, Item?, Item?, string?)"/> over an
    /// already-resolved element type. The raw fn-ptr array declarator
    /// (<c>Ret (*name[N])(params)</c>) computes its element type from the declarator,
    /// not from a type item.</summary>
    private void BuildGlobalArr(CType elem, Item nameItem, Item? dimsItem, Item? initItem, string? csName)
    {
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
            var sym = new Symbol { Name = name, Kind = SymKind.Var, Type = arrType, Storage = Storage.Static, IsGlobal = true, TargetName = csName };
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
    private void BuildGlobalCharArr(Item typeItem, Item nameItem, Item strSeqItem, Item? dimsItem, string? csName, bool wide = false)
    {
        var elem = ResolveType(typeItem);
        var bytes = WideArrValues(elem, strSeqItem, wide);
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
        => BuildStaticLocalArr(ResolveType(typeItem), nameItem, dimsItem, initItem);

    private CStmt BuildStaticLocalArr(CType elem, Item nameItem, Item? dimsItem, Item? initItem)
    {
        var csName = $"{_symbols.Escape(Tok(nameItem))}__s{_staticLocalSeq++}";
        BuildGlobalArr(elem, nameItem, dimsItem, initItem, csName);
        return new DeclStmt(System.Array.Empty<LocalDecl>());
    }

    /// <summary>A block-scope <c>static char a[] = "…"</c> — a pinned global char
    /// array under a mangled name (the statement emits nothing).</summary>
    private CStmt BuildStaticLocalCharArr(Item typeItem, Item nameItem, Item strSeqItem, Item? dimsItem, bool wide = false)
    {
        var csName = $"{_symbols.Escape(Tok(nameItem))}__s{_staticLocalSeq++}";
        BuildGlobalCharArr(typeItem, nameItem, strSeqItem, dimsItem, csName, wide);
        return new DeclStmt(System.Array.Empty<LocalDecl>());
    }
}
