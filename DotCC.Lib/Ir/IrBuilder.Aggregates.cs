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
    /// <summary>A designated element <c>designation = value</c>: the path to the
    /// subobject it initializes, outermost first (value: an InitVal or InitGroup).</summary>
    private sealed record InitDesignated(IReadOnlyList<Designator> Path, Init Value) : Init;

    /// <summary>One step of a designation: <c>.member</c> or <c>[index]</c>.</summary>
    private abstract record Designator;
    private sealed record MemberDesignator(string Name) : Designator;
    private sealed record IndexDesignator(int Index) : Designator;

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
        C.InitElemDesig d => Designated(it, ParseDesignation(d.Arg0), new InitVal(BuildExpr(d.Arg2))),
        C.InitElemDesigNest d => Designated(it, ParseDesignation(d.Arg0), new InitGroup(ParseInitList(d.Arg3))),
        _ => new InitVal(BuildExpr(it)),
    };

    /// <summary>A designated element, gated C99 (as array designators when the path
    /// starts with an index, else as designated initializers).</summary>
    private Init Designated(Item it, List<Designator> path, Init value)
        => Gated(1999, path[0] is IndexDesignator ? "array designators" : "designated initializers", it,
            new InitDesignated(path, value));

    /// <summary>The designator path of a <c>Designation</c>, outermost first.</summary>
    private List<Designator> ParseDesignation(Item designation)
    {
        var path = new List<Designator>();
        void Walk(Item n)
        {
            switch (n.Content)
            {
                case C.DesignationCons c: Walk(c.Arg0); path.Add(ParseDesignator(c.Arg1)); break;
                case C.DesignationOne o: path.Add(ParseDesignator(o.Arg0)); break;
                default: throw new IrUnsupportedException(TypeName(n.Content));
            }
        }
        Walk(designation);
        return path;
    }

    /// <summary>One designator: <c>.member</c>, or <c>[index]</c> with a constant index.</summary>
    private Designator ParseDesignator(Item d) => d.Content switch
    {
        C.DesignatorMember m => new MemberDesignator(Tok(m.Arg1)),
        C.DesignatorIndex ix => new IndexDesignator(ConstEval(BuildExpr(ix.Arg1)) is { } i && i >= 0 ? (int)i
            : throw new IrUnsupportedException("array designator index must be a constant non-negative integer")),
        _ => throw new IrUnsupportedException(TypeName(d.Content)),
    };

    // ---- struct / union aggregates ---------------------------------------

    /// <summary>Build a struct / union aggregate initializer from one brace list by
    /// the current-object walk below: members in declaration order with brace
    /// elision, designators (`.f`, `.a.b`, through anonymous members), a union
    /// through its first member unless a designator names another. Members no
    /// initializer reaches are omitted (C# zero-fills them, C's partial-init rule).</summary>
    private StructInit BuildStructPositional(CType type, IReadOnlyList<Init> items)
    {
        var node = NewNode(type) is { Fields: not null } n ? n
            : throw new IrUnsupportedException("aggregate initializer for a non-struct type");
        FillAggregate(node, items);
        return LowerNode(node) as StructInit ?? throw new IrUnsupportedException("aggregate initializer for a non-struct type");
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

    /// <summary>Interpret a brace initializer against an array TARGET of
    /// <paramref name="elem"/> (constant <paramref name="dims"/>, or null for an
    /// implicit <c>[]</c> sized from the initializer): the dense element list codegen
    /// lays into the array's storage, flattened to the innermost element type (a
    /// struct element as its <see cref="StructInit"/>) and zero-filled to the full
    /// size. Designators, nested braces, brace elision and string rows all go through
    /// the same current-object walk as struct initializers.</summary>
    private List<CExpr> BuildArrayElems(CType elem, IReadOnlyList<int>? dims, IReadOnlyList<Init> items)
    {
        var type = dims is { Count: > 0 } ? MakeArrayType(elem, dims) : new CType.Array(elem, null);
        var node = NewNode(type) ?? throw new IrUnsupportedException("array initializer for a non-array");
        FillAggregate(node, items);
        return FlattenArray(node, full: true);
    }

    // ---- the current-object walk (C11 6.7.9p17-20) -----------------------
    // Every brace-enclosed list has a CURRENT OBJECT. Positional initializers fill
    // its subobjects in order, and a scalar meeting a sub-aggregate descends into it
    // (brace elision: the sub-aggregate takes as many of the following initializers
    // as it needs); a designation (`.v.Name.id`, `[3].x`) moves the cursor to the
    // subobject it names, after which initialization continues forward from there,
    // in the same depth-first order. The walk fills a tree of InitNodes (one per
    // aggregate level being initialized piecewise), which LowerNode / FlattenArray
    // turn into the StructInit / ArrayValue / dense element lists codegen takes.

    /// <summary>
    /// One aggregate level of an initialization in progress: a struct, union or array
    /// object and the values given so far to its direct subobjects, each a
    /// <see cref="CExpr"/> or, for a sub-aggregate filled piecewise, a nested node.
    /// </summary>
    private sealed class InitNode
    {
        public InitNode(CType type, IReadOnlyList<StructField>? fields, bool isUnion, CType.Array? array)
        {
            Type = type;
            Fields = fields;
            IsUnion = isUnion;
            Array = array;
        }

        /// <summary>The object's type.</summary>
        public CType Type { get; }

        /// <summary>A struct or union's initializable members (anonymous bit-fields,
        /// which take no initializer, left out); null for an array.</summary>
        public IReadOnlyList<StructField>? Fields { get; }

        /// <summary>Whether the object is a union (it holds one member's value).</summary>
        public bool IsUnion { get; }

        /// <summary>An array's type (a null Count sizes it from the initializer);
        /// null for a struct or union.</summary>
        public CType.Array? Array { get; }

        /// <summary>The values given so far, by subobject index (a member's position
        /// or an element's index): a <see cref="CExpr"/> or a nested node.</summary>
        public SortedDictionary<int, object> Values { get; } = new();

        /// <summary>The number of direct subobjects (unbounded for an array sized from
        /// its initializer).</summary>
        public int Length => Fields?.Count ?? Array?.Count ?? int.MaxValue;

        /// <summary>The type of subobject <paramref name="index"/>.</summary>
        public CType SubType(int index) => Array is { } a ? a.Element
            : Fields is { } f ? f[index].Type
            : throw new IrUnsupportedException("initializer for a non-aggregate");

        /// <summary>Give subobject <paramref name="index"/> its value (a union keeps
        /// only the member initialized last).</summary>
        public void Store(int index, object value)
        {
            if (IsUnion) { Values.Clear(); }
            Values[index] = value;
        }
    }

    /// <summary>A fresh node for an object of <paramref name="type"/>, or null when
    /// the type is not an aggregate (a scalar or pointer).</summary>
    private InitNode? NewNode(CType type)
    {
        if (type.Unqualified is CType.Array arr) { return new InitNode(type, null, false, arr); }
        if (!IsAggregateType(type)) { return null; }
        var fields = StructFieldsOf(type).Where(f => !f.IsAnonBitField).ToList();
        return new InitNode(type, fields, IsUnionType(type), null);
    }

    /// <summary>Fill <paramref name="root"/> from one brace-enclosed list.</summary>
    private void FillAggregate(InitNode root, IReadOnlyList<Init> items)
    {
        var cursor = new Stack<(InitNode Node, int Index)>();
        cursor.Push((root, 0));
        foreach (var item in items)
        {
            var value = item;
            if (item is InitDesignated d)
            {
                cursor.Clear();
                Designate(root, d.Path, cursor);
                value = d.Value;
            }
            else if (!Settle(cursor))
            {
                throw new IrUnsupportedException($"excess elements in the initializer of '{root.Type.Describe()}'");
            }
            Assign(cursor, value);
        }
    }

    /// <summary>
    /// Move the cursor onto the next existing subobject: pop every level whose
    /// subobjects are used up (an elided sub-aggregate, the tail of a designation
    /// path), stepping its parent past it. False when the whole object is used up.
    /// </summary>
    private static bool Settle(Stack<(InitNode Node, int Index)> cursor)
    {
        while (cursor.Count > 0)
        {
            var (node, index) = cursor.Peek();
            if (index < node.Length) { return true; }
            cursor.Pop();
            if (cursor.Count == 0) { return false; }
            var (parent, at) = cursor.Pop();
            cursor.Push((parent, at + 1));
        }
        return false;
    }

    /// <summary>Step past the subobject under the cursor (past the whole union, for
    /// a union member).</summary>
    private static void Advance(Stack<(InitNode Node, int Index)> cursor)
    {
        var (node, index) = cursor.Pop();
        cursor.Push((node, node.IsUnion ? node.Length : index + 1));
    }

    /// <summary>
    /// Initialize the subobject under the cursor from <paramref name="value"/>: a
    /// braced list initializes it as a whole (its own current object), a string
    /// literal a character array, a scalar a scalar, and a scalar meeting a
    /// sub-aggregate descends into it by brace elision (6.7.9p20).
    /// </summary>
    private void Assign(Stack<(InitNode Node, int Index)> cursor, Init value)
    {
        while (true)
        {
            var (node, index) = cursor.Peek();
            var type = node.SubType(index);
            switch (value)
            {
                case InitGroup group:
                    node.Store(index, BuildSubobject(type, group));
                    Advance(cursor);
                    return;
                case InitVal v when type.Unqualified is CType.Array arr && StringArrayValue(arr, v.Value) is { } str:
                    node.Store(index, str);
                    Advance(cursor);
                    return;
                case InitVal v when !IsWholeValue(type, v.Value) && ChildFor(node, index, type) is { } child:
                    cursor.Push((child, 0));
                    continue;
                case InitVal v:
                    node.Store(index, v.Value);
                    Advance(cursor);
                    return;
                default:
                    throw new IrUnsupportedException("a designation inside a designation");
            }
        }
    }

    /// <summary>The node a brace-elided initializer fills for sub-aggregate
    /// <paramref name="index"/> (the one already under way, or a new one); null when
    /// the subobject is not an aggregate.</summary>
    private InitNode? ChildFor(InitNode node, int index, CType type)
    {
        if (node.Values.TryGetValue(index, out var existing) && existing is InitNode under) { return under; }
        if (NewNode(type) is not { } child) { return null; }
        node.Store(index, child);
        return child;
    }

    /// <summary>A braced initializer for one subobject of <paramref name="type"/>.</summary>
    private object BuildSubobject(CType type, InitGroup group)
    {
        if (type.Unqualified is CType.Array arr && group.Items is [InitVal only] && StringArrayValue(arr, only.Value) is { } str)
        {
            return str;   // `char name[8] = { "…" }`
        }
        if (NewNode(type) is { } node)
        {
            FillAggregate(node, group.Items);
            return node;
        }
        return BracedScalar(group);
    }

    /// <summary>A scalar written in braces, <c>{ 5 }</c> (C11 6.7.9p11).</summary>
    private static CExpr BracedScalar(InitGroup group) => group.Items switch
    {
        [InitVal v] => v.Value,
        [InitGroup inner] => BracedScalar(inner),
        _ => throw new IrUnsupportedException("excess elements in a scalar initializer"),
    };

    /// <summary>Whether <paramref name="value"/> is a whole struct / union of
    /// <paramref name="type"/> (`{ other, 3 }`), which initializes that subobject
    /// as it is rather than starting brace elision.</summary>
    private static bool IsWholeValue(CType type, CExpr value)
        => type.Unqualified is CType.Named tn && value.Type.Unqualified is CType.Named vn && tn.Name == vn.Name;

    /// <summary>
    /// Move the cursor to the subobject a designation names (C11 6.7.9p18), starting
    /// at the list's current object <paramref name="root"/>: each step selects a
    /// member or element, and every step but the last descends into it. The cursor
    /// keeps the whole path, so positional initialization continues after the
    /// designated subobject.
    /// </summary>
    private void Designate(InitNode root, IReadOnlyList<Designator> path, Stack<(InitNode Node, int Index)> cursor)
    {
        var node = root;
        for (var k = 0; k < path.Count; k++)
        {
            switch (path[k])
            {
                case MemberDesignator m when node.Fields is { } fields:
                    node = PushMember(node, fields, m.Name, cursor);
                    break;
                case MemberDesignator m:
                    throw new IrUnsupportedException($"member designator '.{m.Name}' in the initializer of an array");
                case IndexDesignator ix when node.Array is { } arr:
                    if (arr.Count is { } n && ix.Index >= n)
                    {
                        throw new IrUnsupportedException($"array designator index {ix.Index} is out of bounds for [{n}]");
                    }
                    cursor.Push((node, ix.Index));
                    break;
                case IndexDesignator ix:
                    throw new IrUnsupportedException($"array designator [{ix.Index}] in the initializer of '{node.Type.Describe()}'");
            }
            if (k < path.Count - 1)
            {
                var (at, index) = cursor.Peek();
                node = ChildFor(at, index, at.SubType(index))
                    ?? throw new IrUnsupportedException($"a designator into '{at.SubType(index).Describe()}', which is not a struct, union or array");
            }
        }
    }

    /// <summary>Push member <paramref name="name"/> of <paramref name="node"/>. A member
    /// of an anonymous struct or union (C11 6.7.2.1p13) is reached through the hidden
    /// container member dotcc keeps for it; returns the node that holds the member.</summary>
    private InitNode PushMember(InitNode node, IReadOnlyList<StructField> fields, string name, Stack<(InitNode Node, int Index)> cursor)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (fields[i].Name == name)
            {
                cursor.Push((node, i));
                return node;
            }
        }
        if (node.Type.Unqualified is CType.Named owner && _promoted.TryGetValue(owner.Name, out var promoted)
            && promoted.TryGetValue(name, out var via))
        {
            for (var i = 0; i < fields.Count; i++)
            {
                if (fields[i].Name != via.Hidden) { continue; }
                cursor.Push((node, i));
                if (ChildFor(node, i, fields[i].Type) is { Fields: { } inner } holder)
                {
                    return PushMember(holder, inner, name, cursor);
                }
            }
        }
        throw new IrUnsupportedException($"designator '.{name}' names no member of '{(node.Type.Unqualified as CType.Named)?.Name}'");
    }

    /// <summary>The IR value of a filled aggregate: a <see cref="StructInit"/> of the
    /// members given values (C# zero-fills the rest, C's partial-init rule) or, for an
    /// array member, an <see cref="ArrayValue"/> of its flattened elements up to the
    /// last one given.</summary>
    private CExpr LowerNode(InitNode node)
    {
        if (node.Fields is { } fields)
        {
            var members = new List<FieldInit>(node.Values.Count);
            foreach (var (index, value) in node.Values)
            {
                members.Add(new FieldInit(fields[index].Name, fields[index].Type, LowerSlot(value)));
            }
            return new StructInit(members) { Type = node.Type };
        }
        var arr = node.Array ?? throw new IrUnsupportedException("initializer for a non-aggregate");
        return new ArrayValue(arr.FlatElement, FlattenArray(node, full: false)) { Type = arr };
    }

    /// <summary>Split a static object's initializer at its flexible array member (GH
    /// #246): the struct part stays the initializer and the member's elements become the
    /// <see cref="FlexibleTail"/> the backend stores after it. gcc accepts this GNU
    /// extension with a -pedantic warning; a flexible member initialized inside a
    /// nested aggregate is its error.</summary>
    private (CExpr? Init, FlexibleTail? Tail) SplitFlexibleInit(CExpr? init, SrcPos pos)
    {
        if (init is not StructInit si)
        {
            foreach (var inner in NestedValues(init)) { CheckNestedFlexibleInit(inner, pos); }
            return (init, null);
        }
        FlexibleTail? tail = null;
        var members = new List<FieldInit>(si.Members.Count);
        foreach (var m in si.Members)
        {
            if (m.FieldType.Unqualified is CType.Array { Count: null } && m.Value is ArrayValue av)
            {
                if (av.Elems.Count > 0)
                {
                    _gate?.Report("initialization of a flexible array member", pos.Line);
                    tail = new FlexibleTail(m.Name, av.Element, av.Elems);
                }
                continue;
            }
            CheckNestedFlexibleInit(m.Value, pos);
            members.Add(m);
        }
        return (si with { Members = members }, tail);
    }

    /// <summary>An automatic object's initializer may not give its flexible array
    /// member elements (only static storage can hold them); gcc's error.</summary>
    private void CheckNoFlexibleInit(CExpr? init, SrcPos pos)
    {
        if (init is StructInit si && si.Members.Any(IsFlexibleInit))
        {
            Diagnostics.Add(new Diagnostic(Severity.Error, "non-static initialization of a flexible array member", pos, _file));
            return;
        }
        foreach (var inner in NestedValues(init)) { CheckNestedFlexibleInit(inner, pos); }
    }

    /// <summary>A flexible array member given elements by <paramref name="value"/>, the
    /// initializer of a nested aggregate (a struct member or array element), which has
    /// no room for them, or by any aggregate nested in it; gcc's error.</summary>
    private void CheckNestedFlexibleInit(CExpr value, SrcPos pos)
    {
        if (value is StructInit si && si.Members.Any(IsFlexibleInit))
        {
            Diagnostics.Add(new Diagnostic(Severity.Error, "initialization of flexible array member in a nested context", pos, _file));
            return;
        }
        foreach (var inner in NestedValues(value)) { CheckNestedFlexibleInit(inner, pos); }
    }

    /// <summary>The initializers of an aggregate initializer's subobjects (none for a
    /// scalar or an absent one).</summary>
    private static IEnumerable<CExpr> NestedValues(CExpr? init) => init switch
    {
        StructInit s => s.Members.Select(m => m.Value),
        ArrayValue a => a.Elems,
        _ => Enumerable.Empty<CExpr>(),
    };

    /// <summary>A member initializer that gives a flexible array member elements.</summary>
    private static bool IsFlexibleInit(FieldInit m) =>
        m.FieldType.Unqualified is CType.Array { Count: null } && m.Value is ArrayValue { Elems.Count: > 0 };

    /// <summary>A subobject value as IR: a nested node lowered, an expression as is.</summary>
    private CExpr LowerSlot(object value) => value switch
    {
        InitNode n => LowerNode(n),
        CExpr e => e,
        _ => throw new IrUnsupportedException("initializer value"),
    };

    /// <summary>
    /// An array node's elements, flattened to its innermost element type (rows of a
    /// multi-dimensional array inline, always whole so they line up), zero-filling
    /// the elements no initializer reached: all of them when <paramref name="full"/>
    /// (a declaration's storage), else up to the last one given (an array member's
    /// init span).
    /// </summary>
    private List<CExpr> FlattenArray(InitNode node, bool full)
    {
        var arr = node.Array ?? throw new IrUnsupportedException("initializer for a non-array");
        var given = node.Values.Count == 0 ? 0 : node.Values.Keys.Max() + 1;
        var count = full ? arr.Count ?? given : given;
        var outp = new List<CExpr>();
        for (var i = 0; i < count; i++)
        {
            if (node.Values.TryGetValue(i, out var value)) { AppendElement(outp, arr.Element, value); }
            else { AppendZeros(outp, arr.Element); }
        }
        return outp;
    }

    /// <summary>Append one element of type <paramref name="elem"/>, flattened.</summary>
    private void AppendElement(List<CExpr> outp, CType elem, object value)
    {
        if (elem.Unqualified is not CType.Array row)
        {
            outp.Add(LowerSlot(value));
            return;
        }
        switch (value)
        {
            case InitNode rowNode:
                outp.AddRange(FlattenArray(rowNode, full: true));
                break;
            case ArrayValue text:   // a string literal filling a character row
                outp.AddRange(text.Elems);
                for (var k = text.Elems.Count; k < FlatCount(row); k++) { outp.Add(Zero); }
                break;
            default:
                throw new IrUnsupportedException("an array row initialized by a non-array value");
        }
    }

    /// <summary>Append the zero value of one element of type <paramref name="elem"/>,
    /// flattened.</summary>
    private void AppendZeros(List<CExpr> outp, CType elem)
    {
        var flat = elem.Unqualified is CType.Array row ? row.FlatElement : elem;
        var n = elem.Unqualified is CType.Array rows ? FlatCount(rows) : 1;
        for (var k = 0; k < n; k++) { outp.Add(IsAggregateType(flat) ? new DefaultLit { Type = flat } : Zero); }
    }

    /// <summary>The number of innermost elements in <paramref name="arr"/>.</summary>
    private static int FlatCount(CType.Array arr)
        => ConstDimsOf(arr).Aggregate(1, (a, b) => a * b);

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

    /// <summary>A compound literal's type name. C23 lets it take a storage class
    /// (<c>static</c>, <c>constexpr</c>, <c>register</c>, 6.5.2.5p5), which would give
    /// the object a lifetime dotcc does not model yet: a loud cut. A function specifier
    /// is gcc's parse error.</summary>
    private Item CompoundLitType(Item typeItem)
    {
        if (FirstDeclSpec(typeItem) is { } s)
        {
            if (s.Kw is SpecKw.Inline or SpecKw.Noreturn)
            {
                Diagnostics.Add(new Diagnostic(Severity.Error,
                    $"expected expression before '{Spelling(s.Kw)}'", SrcPos.From(s.At), _file));
            }
            else
            {
                throw new IrUnsupportedException($"a compound literal declared '{Spelling(s.Kw)}' (C23 6.5.2.5)");
            }
        }
        return typeItem;
    }

    /// <summary>A struct/union (or scalar) compound literal <c>(T){ … }</c> — an
    /// unnamed object usable in any expression position. A struct lowers to a
    /// <see cref="StructInit"/> (<c>new T { … }</c>); a scalar/pointer/enum to a
    /// single-value cast (<c>(T)(v)</c>).</summary>
    private CExpr BuildCompoundLit(Item typeItem, Item initListItem)
    {
        var type = ResolveType(typeItem);
        if ((type.Unqualified as CType.Named)?.Name is { } canonical && _structFields.ContainsKey(canonical))
        {
            var lit = BuildStructPositional(type, ParseInitList(initListItem));
            CheckNoFlexibleInit(lit, SrcPos.From(initListItem));
            return lit;
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
}
