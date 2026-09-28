#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Item = global::LALR.CC.LexicalGrammar.Item;

namespace DotCC.Ir;

/// <summary>
/// Init-declarators (C11 6.7, 6.7.6, 6.7.9): a declaration is a type followed by a
/// list of declarators, and every declarator form (a plain name, pointers, an array,
/// a function pointer, an array of them, a bit-field) takes part in every list at
/// every scope, with an expression or a brace-enclosed initializer. The grammar
/// parses them as <c>DeclItem</c>s; <see cref="WalkDeclList"/> turns each into a
/// <see cref="Declarator"/> (name, full type, initializer), and each scope decides
/// the storage: a block-scope local (<see cref="BuildLocalArray"/> for an array), a
/// <c>DotCcGlobals</c> field for file scope and <c>static</c> locals
/// (<see cref="BuildStaticArray"/>), a struct member.
/// </summary>
internal sealed partial class IrBuilder
{
    /// <summary>One init-declarator of a declaration.</summary>
    /// <param name="Name">The declared identifier.</param>
    /// <param name="Type">The declaration's base type with the declarator's pointers,
    /// array bounds and function parameters applied. An array declared <c>name[]</c>
    /// has a null outer extent (sized by its initializer, or an <c>extern</c>
    /// declaration, or a flexible array member).</param>
    /// <param name="Init">The initializer: an expression item, or a <c>BraceInit</c>
    /// item (<see cref="C.BraceInit"/> / <see cref="C.BraceInitEmpty"/>); null when there
    /// is none.</param>
    /// <param name="At">The declarator's name token, for diagnostics.</param>
    /// <param name="VlaDims">An array declarator's bounds when one is not an integer
    /// constant expression (the type then has a null outer extent); block scope lowers
    /// a one-dimensional one VLA-style, every other scope rejects it.</param>
    private sealed record Declarator(string Name, CType Type, Item? Init, Item At, Item? VlaDims = null);

    /// <summary>
    /// Walk a comma-separated init-declarator list, invoking <paramref name="add"/>
    /// with each declarator in source order. The FIRST declarator's <c>*</c>s were
    /// greedily folded into the declaration's type by the grammar's
    /// <c>Type → Type *</c> rule; each later declarator rebuilds its type from the
    /// pointer-stripped element plus its own <c>*</c>s (so <c>int *a, b;</c> declares
    /// a:int*, b:int). Shared by block-scope, file-scope and <c>static</c>-local
    /// declarations and by struct / union member lists, which alone pass
    /// <paramref name="addBitField"/> (name, type, width item; the name is empty for an
    /// unnamed bit-field): a bit-field declarator anywhere else is an error.
    /// </summary>
    private void WalkDeclList(Item typeItem, Item listItem, Action<Declarator> add,
        Action<string, CType, Item>? addBitField = null)
    {
        var baseType = ResolveType(typeItem);
        // Peel only the LITERAL trailing `*`s: they bind to the first declarator alone.
        // Pointer-ness a typedef base contributes (`BoxPtr p, q` ⇒ both Box*) is not a
        // literal star and stays in `element`, so every declarator keeps it.
        var litStars = CountLiteralStars(typeItem);
        var element = baseType;
        for (var i = 0; i < litStars && element is CType.Pointer p; i++) { element = p.Pointee; }

        void BitField(string name, Item width, CType type, Item at)
        {
            if (addBitField is null)
            {
                Diagnostics.Add(new Diagnostic(Severity.Error,
                    name.Length == 0 ? "unnamed bit-field outside a struct or union" : $"bit-field '{name}' outside a struct or union",
                    SrcPos.From(at), _file));
                return;
            }
            addBitField(name, type, width);
        }

        // One declarator over the type `t` its own pointers leave for it.
        void Declare(Item it, CType t)
        {
            switch (it.Content)
            {
                case C.DeclItem di: add(new(Tok(di.Arg0), t, null, di.Arg0)); break;
                case C.DeclItemInit di: add(new(Tok(di.Arg0), t, di.Arg2, di.Arg0)); break;
                case C.DeclItemBraceInit di: add(new(Tok(di.Arg0), t, di.Arg2, di.Arg0)); break;
                case C.DeclItemArr da: add(ArrayDeclarator(t, da.Arg0, null)); break;
                case C.DeclItemArrInit da: add(ArrayDeclarator(t, da.Arg0, da.Arg2)); break;
                case C.DeclItemArrBraceInit da: add(ArrayDeclarator(t, da.Arg0, da.Arg2)); break;
                case C.DeclItemBitField bf: BitField(Tok(bf.Arg0), bf.Arg2, t, bf.Arg0); break;
                case C.DeclItemAnonBitField ab: BitField("", ab.Arg1, t, ab.Arg0); break;
                // `Ret (*name)(params) [= E]`: the declarator's base type is the RETURN type.
                case C.DeclItemFnPtr fp: { var (n, ft) = FnPtrDeclarator(t, fp.Arg0, fp.Arg1); add(new(n, ft, null, fp.Arg0)); break; }
                case C.DeclItemFnPtrInit fp: { var (n, ft) = FnPtrDeclarator(t, fp.Arg0, fp.Arg1); add(new(n, ft, fp.Arg3, fp.Arg0)); break; }
                case C.DeclItemFnPtrArr fa: add(FnPtrArrDeclarator(t, fa.Arg0, fa.Arg1, null)); break;
                case C.DeclItemFnPtrArrInit fa: add(FnPtrArrDeclarator(t, fa.Arg0, fa.Arg1, fa.Arg3)); break;
                case C.DeclItemFnPtrArrOpenInit fa: add(FnPtrArrDeclarator(t, fa.Arg0, fa.Arg1, fa.Arg3)); break;
                default: throw new IrUnsupportedException(TypeName(it.Content));
            }
        }

        // A post-comma declarator: `DeclItemTail → * DeclItemTail` adds a pointer
        // level onto the stripped element (`int *a, *b;` → b:int*).
        void WalkTail(Item it, int stars)
        {
            switch (it.Content)
            {
                case C.DeclItemTailPtr p: WalkTail(p.Arg1, stars + 1); break;
                case C.DeclItemTailPlain t: Declare(t.Arg0, WrapPtr(element, stars)); break;
                default: throw new IrUnsupportedException(TypeName(it.Content));
            }
        }

        void Walk(Item it)
        {
            switch (it.Content)
            {
                case C.DeclItemListCons c: Walk(c.Arg0); WalkTail(c.Arg2, 0); break;
                case C.DeclItemListOne o: Declare(o.Arg0, baseType); break;
                default: Declare(it, baseType); break;
            }
        }
        Walk(listItem);
    }

    /// <summary>An array declarator over element type <paramref name="elem"/>: its name
    /// and array type (a null outer extent for <c>name[]</c> / <c>name[][N]</c>), with
    /// non-constant bounds carried as <see cref="Declarator.VlaDims"/>. A parenthesized
    /// declarator <c>(name[N])</c> is the same declarator.</summary>
    private Declarator ArrayDeclarator(CType elem, Item arrDecl, Item? init)
    {
        switch (arrDecl.Content)
        {
            case C.ArrDeclParen p:
                return ArrayDeclarator(elem, p.Arg1, init);
            case C.ArrDeclOpen o:
                return new(Tok(o.Arg0), new CType.Array(elem, null), init, o.Arg0);
            case C.ArrDeclOpenRows o:
            {
                // Only the outermost bound may be omitted (C11 6.7.6.2p1 via 6.2.5p22:
                // the element type must be complete).
                var rows = TryConstDims(o.Arg3)
                    ?? throw new IrUnsupportedException($"'{Tok(o.Arg0)}': the inner bounds of an array declared with `[]` must be constant");
                return new(Tok(o.Arg0), new CType.Array(MakeArrayType(elem, rows), null), init, o.Arg0);
            }
            case C.ArrDeclSized s:
                return TryConstDims(s.Arg1) is { } dims
                    ? new(Tok(s.Arg0), MakeArrayType(elem, dims), init, s.Arg0)
                    : new(Tok(s.Arg0), new CType.Array(elem, null), init, s.Arg0, s.Arg1);
            default:
                throw new IrUnsupportedException(TypeName(arrDecl.Content));
        }
    }

    /// <summary>An array-of-function-pointers declarator <c>Ret (*name[N])(params)</c> /
    /// <c>Ret (*name[])(params)</c> (also <c>*const</c>) over return type
    /// <paramref name="ret"/>.</summary>
    private Declarator FnPtrArrDeclarator(CType ret, Item arrItem, Item tailItem, Item? init)
    {
        var (nameItem, dimsItem, elem) = FnPtrArrParts(ret, arrItem, tailItem);
        if (dimsItem is null) { return new(Tok(nameItem), new CType.Array(elem, null), init, nameItem); }
        var dims = TryConstDims(dimsItem)
            ?? throw new IrUnsupportedException($"'{Tok(nameItem)}': an array of function pointers needs constant bounds");
        return new(Tok(nameItem), MakeArrayType(elem, dims), init, nameItem);
    }

    // ---- initializer values ---------------------------------------------

    /// <summary>The initial value of a non-array object of <paramref name="type"/>:
    /// the expression, or a brace-enclosed initializer. A braced one initializes a
    /// struct or union through the current-object walk, a scalar through its single
    /// (optionally nested-braced) value (C11 6.7.9p11), and the C23 empty <c>{ }</c>
    /// is the zero value.</summary>
    private CExpr BuildInitValue(CType type, Item init)
    {
        switch (init.Content)
        {
            case C.BraceInitEmpty:
                Gate(2023, "empty initializer", init);
                return new DefaultLit { Type = type };
            case C.BraceInit b:
                if (IsAggregateType(type)) { return BuildAggregateInit(type, b.Arg1); }
                return BracedScalar(new InitGroup(ParseInitList(b.Arg1)));
            default:
                var value = BuildExpr(init);
                EnsureNotEmbed(value);
                return value;
        }
    }

    // ---- array objects --------------------------------------------------

    /// <summary>An array object's storage, resolved from its declarator: the complete
    /// type (an open <c>[]</c> extent sized by the initializer), the innermost element
    /// type its flat storage holds, the number of such elements, and their values
    /// (dense, zero-filled; null when the array is not initialized, which C# storage
    /// zero-fills).</summary>
    private sealed record ArrayObject(CType.Array Type, CType Elem, int Count, List<CExpr>? Elems);

    /// <summary>Resolve an array declarator's storage from its initializer: none (the
    /// type must be complete), a brace-enclosed list (the current-object walk, which
    /// also sizes an open extent), the C23 empty <c>{ }</c>, or a string literal for a
    /// character array (C11 6.7.9p14, optionally braced). Any other initializer is
    /// C's constraint violation.</summary>
    private ArrayObject ResolveArrayObject(Declarator d, CType.Array arr)
    {
        var elem = arr.FlatElement;
        if (d.Init is not { } init) { return new(arr, elem, CompleteFlatCount(d, arr), null); }
        switch (init.Content)
        {
            case C.BraceInitEmpty:
                Gate(2023, "empty initializer", init);
                return new(arr, elem, CompleteFlatCount(d, arr), null);
            case C.BraceInit b:
            {
                var items = ParseInitList(b.Arg1);
                // `char s[] = { "…" }`: the string literal, optionally enclosed in braces.
                if (items is [InitVal { Value: var sole }] && StringArrayElems(arr, sole) is { } braced)
                {
                    return braced;
                }
                var node = NewNode(arr) ?? throw new IrUnsupportedException("array initializer for a non-array");
                FillAggregate(node, items);
                var elems = FlattenArray(node, full: true);
                var complete = arr.Count is null
                    ? arr with { Count = node.Values.Count == 0 ? 0 : node.Values.Keys.Max() + 1 }
                    : arr;
                return new(complete, elem, elems.Count, elems);
            }
            default:
            {
                var value = BuildExpr(init);
                return StringArrayElems(arr, value)
                    ?? throw new IrUnsupportedException(value is LitStr or LitU16Str or LitU32Str
                        ? $"'{d.Name}': a string literal of this width cannot initialize an array of '{arr.Element.Describe()}'"
                        : $"'{d.Name}': an array initializer must be an initializer list or a string literal");
            }
        }
    }

    /// <summary>The flat element count of an uninitialized array, which must be
    /// complete (gcc's "array size missing").</summary>
    private static int CompleteFlatCount(Declarator d, CType.Array arr)
        => arr.Count is null
            ? throw new IrUnsupportedException($"array size missing in '{d.Name}'")
            : FlatCount(arr);

    /// <summary>A one-dimensional character array initialized by a string literal of
    /// the element's width: the code units and the NUL, zero-padded to a declared
    /// extent (or truncated, where an exact fit drops the NUL, C's rule); an open
    /// extent takes the literal's length. Null when <paramref name="value"/> is not such
    /// a literal.</summary>
    private static ArrayObject? StringArrayElems(CType.Array arr, CExpr value)
    {
        if (StringArrayValue(arr, value) is not { } text) { return null; }
        var complete = arr.Count is null ? arr with { Count = text.Elems.Count } : arr;
        var count = complete.Count ?? text.Elems.Count;
        var elems = new List<CExpr>(text.Elems);
        while (elems.Count < count) { elems.Add(Zero); }
        return new(complete, arr.Element, count, elems);
    }

    /// <summary>A block-scope array: a C# <c>stackalloc</c> of its flat storage, the
    /// symbol keeping the (possibly nested) array type so subscripts stride and
    /// <c>sizeof</c> / the length idiom resolve. A runtime extent (VLA-style) decays to
    /// a pointer over a <c>stackalloc</c> of that length.</summary>
    private ArrayDecl BuildLocalArray(Declarator d, CType.Array arr)
    {
        if (d.VlaDims is { } vla)
        {
            if (d.Init is not null)
            {
                throw new IrUnsupportedException($"variable-sized object '{d.Name}' may not be initialized");
            }
            var extents = BuildArrDims(vla);
            if (extents.Count != 1)
            {
                throw new IrUnsupportedException("multi-dimensional array with a non-constant dimension");
            }
            var vsym = _symbols.Declare(new Symbol { Name = d.Name, Kind = SymKind.Var, Type = new CType.Pointer(arr.Element), Storage = Storage.Auto });
            return new ArrayDecl(vsym, arr.Element, extents[0], null);
        }
        var a = ResolveArrayObject(d, arr);
        var sym = _symbols.Declare(new Symbol { Name = d.Name, Kind = SymKind.Var, Type = a.Type, Storage = Storage.Auto });
        return new ArrayDecl(sym, a.Elem, a.Elems is null ? CountLit(a.Count) : null, a.Elems);
    }

    /// <summary>An array with static storage duration (file scope, or a block-scope
    /// <c>static</c> under the mangled <paramref name="csName"/>): a pinned, rooted
    /// managed array behind a <c>T*</c> field, which persists for the program's
    /// lifetime (a <c>stackalloc</c> cannot).</summary>
    private void BuildStaticArray(Declarator d, CType.Array arr, string? csName)
    {
        if (d.VlaDims is not null)
        {
            throw new IrUnsupportedException($"storage size of '{d.Name}' isn't constant");
        }
        var a = ResolveArrayObject(d, arr);
        var init = new PinnedArray(a.Elem, a.Elems, a.Elems is null ? CountLit(a.Count) : null) { Type = new CType.Pointer(a.Elem) };
        AddGlobalArray(d.Name, a.Type, init, csName);
    }

    /// <summary>An element count as an <c>int</c> literal.</summary>
    private static LitInt CountLit(int n) => new(n.ToString(System.Globalization.CultureInfo.InvariantCulture), n) { Type = CType.Int };

    /// <summary>The type a declarator gives a struct or union member. An array member's
    /// bounds must be constant (codegen: a <c>fixed</c> buffer for a primitive element,
    /// an [InlineArray] wrapper otherwise; a multi-dimensional one strides through its
    /// nested type); a C99 flexible array member <c>T name[]</c> is modeled as one
    /// element (the struct-hack convention: the member exists and access over-indexes
    /// into the tail). A member takes no initializer.</summary>
    private CType MemberDeclaratorType(Declarator d, Item member)
    {
        if (d.Init is not null)
        {
            throw new IrUnsupportedException($"struct or union member '{d.Name}' cannot have an initializer");
        }
        if (d.Type.Unqualified is not CType.Array arr) { return d.Type; }
        if (d.VlaDims is not null) { throw new IrUnsupportedException("non-constant struct array bound"); }
        if (arr.Count is not null) { return d.Type; }
        Gate(1999, "flexible array member", member);
        return arr with { Count = 1 };
    }
}
