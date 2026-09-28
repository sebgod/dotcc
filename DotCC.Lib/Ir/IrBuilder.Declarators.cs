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
        // A typedef's naming hint belongs to its own type specifier only, never to
        // a type its declarators resolve (a parameter's anonymous struct).
        _anonTagHint = null;
        // Peel only the LITERAL trailing `*`s: they bind to the first declarator alone.
        // Pointer-ness a typedef base contributes (`BoxPtr p, q` ⇒ both Box*) is not a
        // literal star and stays in `element`, so every declarator keeps it.
        var litStars = CountLiteralStars(typeItem);
        var element = baseType;
        for (var i = 0; i < litStars; i++)
        {
            // A literal star made a pointer, or the fn-ptr of a function type.
            if (element is CType.Pointer p) { element = p.Pointee; }
            else if (element is CType.Func { IsFunctionType: false } f) { element = f with { IsFunctionType = true }; }
            else { break; }
        }

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
                // A function declarator declares a function (or, in a typedef, names
                // a function type): its type is the function type, not the fn-ptr.
                case C.DeclItemFn f: add(new(Tok(f.Arg0), FunctionType(t, f.Arg1), null, f.Arg0)); break;
                case C.DeclItemParenFn f: add(new(Tok(f.Arg1), FunctionType(t, f.Arg3), null, f.Arg1)); break;
                case C.DeclItemFnRetFnPtr f:
                    add(new(Tok(f.Arg2), FnPtrType(FnPtrTailType(t, f.Arg7), f.Arg4) with { IsFunctionType = true }, null, f.Arg2));
                    break;
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

    /// <summary>The function type a function declarator's parameter tail gives over
    /// return type <paramref name="ret"/>.</summary>
    private CType.Func FunctionType(CType ret, Item tail) => FnPtrTailType(ret, tail) with { IsFunctionType = true };

    /// <summary>Whether a declarator list declares a function (such a declaration is
    /// a prototype, which a re-included header repeats harmlessly, so it is never
    /// deduplicated as a header-defined variable is).</summary>
    private static bool DeclaresFunction(Item listItem) => listItem.Content switch
    {
        C.DeclItemListCons c => DeclaresFunction(c.Arg0) || DeclaresFunction(c.Arg2),
        C.DeclItemListOne o => DeclaresFunction(o.Arg0),
        C.DeclItemTailPtr t => DeclaresFunction(t.Arg1),
        C.DeclItemTailPlain t => DeclaresFunction(t.Arg0),
        C.DeclItemFn or C.DeclItemParenFn or C.DeclItemFnRetFnPtr => true,
        _ => false,
    };

    /// <summary>A function declarator outside a definition: a prototype. Declares the
    /// function in the current scope (file scope, or a block for a block-scope
    /// declaration), applies its function specifiers and attributes, and tracks a
    /// declared-but-undefined function as a native-import candidate.</summary>
    private void DeclareFunctionDeclarator(Declarator d, CType.Func fn, bool internalLinkage)
    {
        var ps = new List<ParamInfo>(fn.Params.Count);
        for (var i = 0; i < fn.Params.Count; i++) { ps.Add(new ParamInfo(fn.Params[i], "_p" + i)); }
        var sig = new FnSig(fn.Return, d.Name, ps, fn.Variadic, internalLinkage);
        // The declarator's position is its name token; the whole declaration lives in
        // one file, so the system-header band check is reliable.
        var sym = DeclareFunc(sig, fromSystemHeader: d.At.Position.Line >= SrcPos.SyntheticLineBase);
        ApplyFnMarkers(sym);
        // Import-mode candidate tracking: a prototype not (yet) defined in any TU is a
        // potential native `-l` import; a later definition retracts it (BuildFuncDef).
        if (!_fnDefSites.ContainsKey(sig.Name)) { _protoOnlyFuncs[sig.Name] = sym; }
    }

    /// <summary>A block-scope <c>extern T x…;</c> (C11 6.2.2p4): a function declarator
    /// declares the function; an object declarator refers to the file-scope object of
    /// that name for the rest of the block (declared as an extern if none is known
    /// yet). Nothing is emitted.</summary>
    private void BuildBlockExternDecls(Item typeItem, Item listItem, bool forInit)
    {
        _sawNoreturnSpec = false;
        _sawInlineSpec = false;
        WalkDeclList(typeItem, listItem, d =>
        {
            if (d.Type is CType.Func { IsFunctionType: true } fnType)
            {
                if (forInit) { ForInitNonVariable(d); }
                DeclareFunctionDeclarator(d, fnType, internalLinkage: false);
                return;
            }
            if (forInit)
            {
                _gate?.Report($"declaration of 'extern' variable '{d.Name}' in 'for' loop initial declaration", d.At.Position.Line);
            }
            if (d.Init is not null)
            {
                Diagnostics.Add(new Diagnostic(Severity.Error,
                    $"'{d.Name}' has both 'extern' and initializer", SrcPos.From(d.At), _file));
                return;
            }
            var type = d.Type.Unqualified is CType.Array { Count: null } open ? new CType.Pointer(open.Element) : d.Type;
            _symbols.DeclareAlias(new Symbol
            {
                Name = d.Name, Kind = SymKind.Var, Type = type, Storage = Storage.Extern, IsGlobal = true,
                TargetName = _symbols.Escape(d.Name),
            });
        });
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

    // ---- tag definitions and typedefs -------------------------------------

    /// <summary>The types the tag definitions resolved so far define, by their Type
    /// item: however often a declaration's type is resolved, its definition runs once
    /// (a re-included header brings new items, and is deduped by tag instead).</summary>
    private readonly Dictionary<Item, CType> _tagDefs = new(ReferenceEqualityComparer.Instance);

    /// <summary>The name an anonymous tag definition takes when it is the type of a
    /// typedef's first, plain declarator (<c>typedef struct { … } Foo;</c> emits the
    /// C# type <c>Foo</c>, not a synthesized <c>__AnonN</c>). Set by
    /// <see cref="BuildTypedefs"/>, taken by the first anonymous definition resolved.</summary>
    private string? _anonTagHint;

    /// <summary>Take (and clear) <see cref="_anonTagHint"/>, so a nested anonymous
    /// aggregate inside the named one never sees it.</summary>
    private string? TakeAnonTagHint()
    {
        var hint = _anonTagHint;
        _anonTagHint = null;
        return hint;
    }

    /// <summary>A struct or union definition used as a type: defined under its tag,
    /// under the typedef name an anonymous one is given, or as a synthesized
    /// <c>__AnonN</c>.</summary>
    private CType DefineAggregate(Item typeItem, string? tag, Item members, bool isUnion)
    {
        if (_tagDefs.TryGetValue(typeItem, out var done)) { return done; }
        // Taken before the members resolve, tagged or not: the hint names the
        // declaration's own type, never an anonymous aggregate nested inside it.
        var hint = TakeAnonTagHint();
        CType type;
        if (tag is not null)
        {
            BuildStructDef(tag, members, null, isUnion);
            type = new CType.Named(tag);
        }
        else if (hint is { } alias)
        {
            BuildStructDef(null, members, alias, isUnion);
            type = new CType.Named(alias);
        }
        else
        {
            type = ResolveAnonAggregate(typeItem, members, isUnion);
        }
        _tagDefs[typeItem] = type;
        return type;
    }

    /// <summary>An enum definition used as a type: a real C# enum named by its tag or
    /// by the typedef name an anonymous one is given, else (untagged, un-typedef'd)
    /// plain int constants.</summary>
    private CType DefineEnum(Item typeItem, string? tag, Item? baseType, Item list)
    {
        if (_tagDefs.TryGetValue(typeItem, out var done)) { return done; }
        var hint = TakeAnonTagHint();
        var type = RegisterEnum(tag, baseType, list, tag is null ? hint : null);
        _tagDefs[typeItem] = type;
        return type;
    }

    /// <summary>Whether a type specifier declares something on its own: a tag
    /// (defined or forward-declared) or enumeration constants.</summary>
    private static bool DeclaresTag(Item typeItem) => typeItem.Content switch
    {
        C.TypeStruct or C.TypeUnion or C.TypeEnum
            or C.TypeStructDef or C.TypeUnionDef or C.TypeEnumDef or C.TypeEnumAnonDef or C.TypeEnumDefTyped => true,
        C.TypeDeclSpec q => DeclaresTag(q.Arg1),
        C.TypeDeclSpecPost q => DeclaresTag(q.Arg0),
        C.TypeConstPre q => DeclaresTag(q.Arg1),
        C.TypeConstPost q => DeclaresTag(q.Arg0),
        C.TypeVolatile q => DeclaresTag(q.Arg1),
        C.TypeVolatilePost q => DeclaresTag(q.Arg0),
        _ => false,
    };

    /// <summary>A declaration with no declarators (<c>struct Node { … };</c>,
    /// <c>struct Node;</c>, <c>enum { A, B };</c>): resolving its type defines what it
    /// declares. One that declares nothing gets gcc's warnings.</summary>
    private void BuildTagDecl(Item typeItem)
    {
        CheckDeclSpecs(typeItem);
        ResolveType(typeItem);
        var core = typeItem;
        while (core.Content is C.TypeDeclSpec or C.TypeDeclSpecPost)
        {
            core = core.Content switch { C.TypeDeclSpec pre => pre.Arg1, C.TypeDeclSpecPost post => post.Arg0, _ => core };
        }
        if (core.Content is C.TypeStructAnonDef or C.TypeUnionAnonDef)
        {
            Diagnostics.Add(new Diagnostic(Severity.Warning, "unnamed struct/union that defines no instances", SrcPos.From(typeItem), _file));
            return;
        }
        if (!DeclaresTag(core))
        {
            Diagnostics.Add(new Diagnostic(Severity.Warning, "useless type name in empty declaration", SrcPos.From(typeItem), _file));
            return;
        }
        // A declaration that declares only a tag has no object for a specifier to
        // apply to (C11 6.7p2); gcc's diagnostics.
        foreach (var s in DeclSpecsOf(typeItem))
        {
            var (severity, msg) = s.Kw switch
            {
                SpecKw.Inline or SpecKw.Noreturn => (Severity.Error, $"'{Spelling(s.Kw)}' in empty declaration"),
                SpecKw.ThreadLocal or SpecKw.Constexpr => (Severity.Warning, $"useless '{Spelling(s.Kw)}' in empty declaration"),
                _ => (Severity.Warning, "useless storage class specifier in empty declaration"),
            };
            Diagnostics.Add(new Diagnostic(severity, msg, SrcPos.From(s.At), _file));
        }
    }

    /// <summary>A struct or union member declaration with no declarators: a C11
    /// anonymous struct or union, whose members are promoted into the parent, or a
    /// nested tag definition (C declares the tag at file scope).</summary>
    private void BuildMemberTagDecl(Item typeItem, string owner, List<StructField> fields, Item member)
    {
        switch (typeItem.Content)
        {
            case C.TypeStructAnonDef a:
                Gate(2011, "anonymous struct/union member", member);
                AddAnonMember(a.Arg2, owner, fields, isUnion: false);
                return;
            case C.TypeUnionAnonDef a:
                Gate(2011, "anonymous struct/union member", member);
                AddAnonMember(a.Arg2, owner, fields, isUnion: true);
                return;
        }
        ResolveType(typeItem);
        if (!DeclaresTag(typeItem))
        {
            Diagnostics.Add(new Diagnostic(Severity.Warning, "declaration does not declare anything", SrcPos.From(typeItem), _file));
        }
    }

    /// <summary><c>typedef T d1, d2…;</c> at file or block scope: each declarator
    /// names an alias of its full type (C11 6.7.8). An anonymous tag definition is
    /// named after the first declarator when that is a plain name.</summary>
    private void BuildTypedefs(Item typeItem, Item listItem, bool forInit = false)
    {
        _anonTagHint = CountLiteralStars(typeItem) == 0 ? FirstPlainDeclarator(listItem) : null;
        try
        {
            WalkDeclList(typeItem, listItem, d =>
            {
                if (forInit) { ForInitNonVariable(d); }
                if (d.Init is not null) { throw new IrUnsupportedException($"typedef '{d.Name}' is initialized"); }
                if (d.VlaDims is not null) { throw new IrUnsupportedException($"typedef '{d.Name}': a variably modified typedef is not supported"); }
                DeclareTypedef(d.Name, d.Type);
            });
        }
        finally
        {
            _anonTagHint = null;
        }
    }

    /// <summary>A <c>for</c> loop's initial declaration declares something other than
    /// an object (a typedef or a function, C11 6.8.5p3): gcc's pedantic warning.</summary>
    private void ForInitNonVariable(Declarator d) =>
        _gate?.Report($"declaration of non-variable '{d.Name}' in 'for' loop initial declaration", d.At.Position.Line);

    /// <summary>The name of a declarator list's first declarator when it is a plain
    /// identifier (no pointer, array or function part), else null.</summary>
    private static string? FirstPlainDeclarator(Item listItem) => listItem.Content switch
    {
        C.DeclItemListCons c => FirstPlainDeclarator(c.Arg0),
        C.DeclItemListOne o => o.Arg0.Content is C.DeclItem di ? Tok(di.Arg0) : null,
        C.DeclItem di => Tok(di.Arg0),
        _ => null,
    };

    /// <summary>Bind a typedef name: at file scope in the program-wide typedef table,
    /// in a block as a scoped symbol that ends with the block (a block-scope typedef
    /// may shadow a file-scope one of the same name).</summary>
    private void DeclareTypedef(string name, CType type)
    {
        if (_symbols.AtFileScope)
        {
            _typedefs[name] = type;
            return;
        }
        _symbols.DeclareAlias(new Symbol
        {
            Name = name, Kind = SymKind.Typedef, Type = type, Storage = Storage.Typedef, TargetName = name,
        });
    }

    /// <summary>The type a declarator gives a struct or union member. An array member's
    /// bounds must be constant (codegen: a <c>fixed</c> buffer for a primitive element,
    /// an [InlineArray] wrapper otherwise; a multi-dimensional one strides through its
    /// nested type). A C99 flexible array member <c>T name[]</c> keeps its incomplete
    /// array type and a GNU zero-length one <c>T name[0]</c> its zero extent: neither
    /// has storage of its own, the layout model places it after the members before it
    /// (aligned to its element), and an access is its address in the object (GH #246).
    /// A member takes no initializer.</summary>
    private CType MemberDeclaratorType(Declarator d, Item member)
    {
        if (d.Init is not null)
        {
            throw new IrUnsupportedException($"struct or union member '{d.Name}' cannot have an initializer");
        }
        if (d.Type is CType.Func { IsFunctionType: true } fn)
        {
            // Reported, then kept as the fn-ptr so the rest of the layout still builds.
            Diagnostics.Add(new Diagnostic(Severity.Error,
                $"field '{d.Name}' declared as a function", SrcPos.From(d.At), _file));
            return fn with { IsFunctionType = false };
        }
        if (d.Type.Unqualified is not CType.Array arr) { return d.Type; }
        if (d.VlaDims is not null) { throw new IrUnsupportedException("non-constant struct array bound"); }
        if (arr.Count is 0)
        {
            _gate?.Report($"ISO C forbids zero-size array '{d.Name}'", d.At.Position.Line);
        }
        if (arr.Count is null) { Gate(1999, "flexible array member", member); }
        return d.Type;
    }
}
