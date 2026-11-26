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
/// parses them as <c>InitDeclarator</c>s; <see cref="WalkDeclList"/> turns each into a
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
    /// item (<see cref="C.BraceInit"/>, a null InitList for the C23 empty <c>{ }</c>); null when there
    /// is none.</param>
    /// <param name="At">The declarator's name token, for diagnostics.</param>
    /// <param name="VlaDims">An array declarator's bound expressions, outermost first,
    /// when one is not an integer constant expression (the type then has a null outer
    /// extent); block scope lowers a one-dimensional one VLA-style, every other scope
    /// rejects it.</param>
    private sealed record Declarator(string Name, CType Type, Item? Init, Item At, IReadOnlyList<Item>? VlaDims = null);

    /// <summary>What a declarator (C11 6.7.6) or an abstract declarator (6.7.7) gives
    /// over a base type.</summary>
    /// <param name="Name">The declared identifier's token; null for an abstract
    /// declarator.</param>
    /// <param name="Type">The base type with the declarator's pointers, array bounds and
    /// function parameters applied.</param>
    /// <param name="Params">The parameters of a function declarator, the one whose
    /// parameter list follows the name itself (<c>f(int a)</c>, <c>(f)(int a)</c>,
    /// <c>(*f(int a))(int)</c>); null for any other declarator. A function definition
    /// takes its parameter names from it.</param>
    /// <param name="Variadic">Whether <paramref name="Params"/> ends in <c>...</c>.</param>
    /// <param name="VlaDims">See <see cref="Declarator.VlaDims"/>.</param>
    private readonly record struct DeclaratorParts(
        Item? Name, CType Type, List<ParamInfo>? Params = null, bool Variadic = false, IReadOnlyList<Item>? VlaDims = null);

    /// <summary>
    /// Fold a declarator into the type it declares, inside out (C11 6.7.6): a pointer
    /// applies to what is left of it, an array bound or a parameter list to the direct
    /// declarator before it, so <c>*a[3]</c> is an array of pointers and
    /// <c>(*a)[3]</c> a pointer to an array. <paramref name="t"/> is the type the
    /// declarator is applied to: the declaration's specifiers at the top, then each
    /// derived type on the way in. One fold serves every declarator in every position
    /// (a declaration, a member, a parameter, a function definition) and the abstract
    /// ones of a type name.
    /// </summary>
    private DeclaratorParts DeclaratorType(CType t, Item it)
    {
        switch (it.Content)
        {
            case C.Declarator d: return DeclaratorType(PointerOver(t, d.Arg0), d.Arg1);
            case C.DeclaratorName n: return new(n.Arg0, t);
            case C.DeclaratorParen p: return DeclaratorType(t, p.Arg1);
            case C.DeclaratorFunction f:
            {
                var fn = FunctionOver(t, f.Arg2, out var ps, out var variadic);
                var inner = DeclaratorType(fn, f.Arg0);
                return IsBareName(f.Arg0) ? inner with { Params = ps, Variadic = variadic } : inner;
            }
            case C.DeclaratorArray:
            {
                // `a[2][3]` is ((a [2]) [3]): the bounds from here inwards are the
                // array's, innermost first.
                var bounds = new List<Item?>();
                var of = it;
                while (of.Content is C.DeclaratorArray a) { bounds.Insert(0, a.Arg2); of = a.Arg0; }
                var arr = ArrayOver(t, bounds, it, out var vla);
                var inner = DeclaratorType(arr, of);
                if (vla is null) { return inner; }
                return IsBareName(of)
                    ? inner with { VlaDims = vla }
                    : throw new IrUnsupportedException("a pointer to (or function returning) an array needs constant bounds");
            }
            case C.AbstractPointer a: return new(null, PointerOver(t, a.Arg0));
            case C.AbstractDeclarator a: return DeclaratorType(PointerOver(t, a.Arg0), a.Arg1);
            case C.AbstractParen p: return DeclaratorType(t, p.Arg1);
            case C.AbstractFunction f:
            {
                var fn = FunctionOver(t, f.Arg2, out _, out _);
                return f.Arg0 is { } of ? DeclaratorType(fn, of) : new(null, fn);
            }
            case C.AbstractArray:
            {
                var bounds = new List<Item?>();
                Item? of = it;
                while (of is { Content: C.AbstractArray a }) { bounds.Insert(0, a.Arg2); of = a.Arg0; }
                var arr = ArrayOver(t, bounds, it, out var vla);
                if (of is null) { return new(null, arr, VlaDims: vla); }
                return vla is null
                    ? DeclaratorType(arr, of)
                    : throw new IrUnsupportedException("a pointer to (or function returning) an array needs constant bounds");
            }
            default: throw new IrUnsupportedException(TypeName(it.Content));
        }
    }

    /// <summary>The type a Pointer (<c>*</c>, <c>*const</c>, <c>**</c>, …) gives over
    /// <paramref name="t"/>, its stars applied in source order; <paramref name="t"/>
    /// itself for a null Pointer (the optional one absent). A <c>const</c> after a
    /// <c>*</c> makes that pointer read-only (<c>p = q</c> trips the const check,
    /// <c>*p = v</c> does not); <c>volatile</c> and <c>restrict</c> have no C# model
    /// and are accepted.</summary>
    private static CType PointerOver(CType t, Item? pointer)
    {
        static CType Qualified(CType p, Item? quals) => QualsHaveConst(quals) ? p.WithQuals(TypeQual.Const) : p;
        return pointer?.Content switch
        {
            null => t,
            C.Pointer p => Qualified(PointerTo(t), p.Arg1),
            C.PointerCons p => Qualified(PointerTo(PointerOver(t, p.Arg0)), p.Arg2),
            { } other => throw new IrUnsupportedException(TypeName(other)),
        };
    }

    /// <summary>Whether a TypeQualList (the qualifiers after a declarator's <c>*</c>)
    /// includes <c>const</c>. A null list (the optional one absent) has none.</summary>
    private static bool QualsHaveConst(Item? quals) => quals?.Content switch
    {
        null => false,
        C.TypeQualConst => true,
        C.TypeQualVolatile or C.TypeQualRestrict => false,
        C.TypeQualListCons q => QualsHaveConst(q.Arg0) || QualsHaveConst(q.Arg1),
        { } other => throw new IrUnsupportedException(TypeName(other)),
    };

    /// <summary>The function type a parameter list gives over return type
    /// <paramref name="ret"/>, with the parameters as declared (names included). A
    /// null list (<c>()</c>) and <c>(void)</c> both declare no parameters.</summary>
    private CType.Func FunctionOver(CType ret, Item? paramList, out List<ParamInfo> ps, out bool variadic)
    {
        ps = BuildParams(paramList, out variadic);
        var types = new List<CType>(ps.Count);
        foreach (var p in ps) { types.Add(p.Type); }
        return new CType.Func(ret, types, variadic) { IsFunctionType = true };
    }

    /// <summary>The array type a run of bounds gives over element type
    /// <paramref name="elem"/>, outermost bound first; a null bound is <c>[]</c>. Only
    /// the outermost may be omitted (C11 6.7.6.2p1 via 6.2.5p22: the element type must
    /// be complete), which leaves a null outer extent (sized by the initializer, an
    /// <c>extern</c> declaration, a flexible array member, a parameter); an inner one
    /// omitted is gcc's error, reported at <paramref name="at"/> (the declarator). When
    /// a bound is not an integer constant expression the type has a null outer extent
    /// and <paramref name="vla"/> carries the bound expressions.</summary>
    private CType ArrayOver(CType elem, List<Item?> bounds, Item at, out IReadOnlyList<Item>? vla)
    {
        vla = null;
        var who = DeclaredName(at) is { } name ? $"'{Tok(name)}': " : "";
        var open = bounds[0] is null;
        var sized = new List<Item>(bounds.Count);
        for (var i = open ? 1 : 0; i < bounds.Count; i++)
        {
            if (bounds[i] is { } bound) { sized.Add(bound); continue; }
            // Reported, then left out so the rest of the declaration still binds.
            Diagnostics.Add(new Diagnostic(Severity.Error,
                DeclaredName(at) is { } n ? $"array type has incomplete element type in '{Tok(n)}'" : "array type has incomplete element type",
                SrcPos.From(at), _file));
        }
        var dims = new List<int>(sized.Count);
        foreach (var b in sized)
        {
            if (ConstEval(BuildExpr(b)) is { } n) { dims.Add((int)n); continue; }
            if (open)
            {
                throw new IrUnsupportedException($"{who}the inner bounds of an array declared with `[]` must be constant");
            }
            vla = sized;
            return new CType.Array(elem, null);
        }
        var rows = MakeArrayType(elem, dims);
        return open ? new CType.Array(rows, null) : rows;
    }

    /// <summary>The name token a declarator declares, or null for an abstract one.</summary>
    private static Item? DeclaredName(Item declarator) => declarator.Content switch
    {
        C.Declarator d => DeclaredName(d.Arg1),
        C.DeclaratorName n => n.Arg0,
        C.DeclaratorParen p => DeclaredName(p.Arg1),
        C.DeclaratorArray a => DeclaredName(a.Arg0),
        C.DeclaratorFunction f => DeclaredName(f.Arg0),
        _ => null,
    };

    /// <summary>Whether a direct declarator is the declared name itself, alone or in
    /// grouping parentheses (<c>f</c>, <c>(f)</c>): what an array bound or a parameter
    /// list right after it applies to.</summary>
    private static bool IsBareName(Item direct) => direct.Content switch
    {
        C.DeclaratorName => true,
        C.DeclaratorParen { Arg1.Content: C.Declarator { Arg0: null } d } => IsBareName(d.Arg1),
        _ => false,
    };

    /// <summary>Whether a declarator is a function declarator: a parameter list
    /// follows the name itself, so it declares a function (<c>f(int)</c>,
    /// <c>*f(int)</c>, <c>(f)(int)</c>, <c>(*f(int))(int)</c>), where
    /// <c>(*fp)(int)</c> declares a pointer.</summary>
    private static bool IsFunctionDeclarator(Item declarator) => declarator.Content switch
    {
        C.Declarator d => IsFunctionDeclarator(d.Arg1),
        C.DeclaratorParen p => IsFunctionDeclarator(p.Arg1),
        C.DeclaratorArray a => IsFunctionDeclarator(a.Arg0),
        C.DeclaratorFunction f => IsBareName(f.Arg0) || IsFunctionDeclarator(f.Arg0),
        _ => false,
    };

    /// <summary>
    /// Walk a comma-separated init-declarator list, invoking <paramref name="add"/>
    /// with each declarator in source order. Each declarator applies its own pointers,
    /// array bounds and parameters to the declaration's specifiers (so
    /// <c>int *a, b;</c> declares a:int*, b:int). Shared by block-scope, file-scope and
    /// <c>static</c>-local declarations and by struct / union member lists, which alone
    /// pass <paramref name="addBitField"/> (name, type, width item; the name is empty
    /// for an unnamed bit-field): a bit-field declarator anywhere else is an error.
    /// </summary>
    private void WalkDeclList(Item typeItem, Item listItem, Action<Declarator> add,
        Action<string, CType, Item>? addBitField = null)
    {
        var baseType = ResolveType(typeItem);
        // A typedef's naming hint belongs to its own type specifier only, never to
        // a type its declarators resolve (a parameter's anonymous struct).
        _anonTagHint = null;

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

        Declarator Declared(Item declarator, Item? init)
        {
            var p = DeclaratorType(baseType, declarator);
            var name = p.Name ?? throw new IrUnsupportedException(TypeName(declarator.Content));
            return new(Tok(name), p.Type, init, name, p.VlaDims);
        }

        void Declare(Item it)
        {
            switch (it.Content)
            {
                case C.InitDeclarator d: add(Declared(d.Arg0, null)); break;
                // An Initializer (Arg2) is an expression item or a BraceInit item.
                case C.InitDeclaratorInit d: add(Declared(d.Arg0, d.Arg2)); break;
                // An unnamed bit-field `: width` has a null Declarator.
                case C.BitFieldDeclarator { Arg0: null } bf: BitField("", bf.Arg2, baseType, bf.Arg1); break;
                case C.BitFieldDeclarator { Arg0: { } declarator } bf:
                {
                    var d = Declared(declarator, null);
                    BitField(d.Name, bf.Arg2, d.Type, d.At);
                    break;
                }
                default: throw new IrUnsupportedException(TypeName(it.Content));
            }
        }

        void Walk(Item it)
        {
            switch (it.Content)
            {
                case C.InitDeclaratorListCons c: Walk(c.Arg0); Declare(c.Arg2); break;
                case C.InitDeclaratorListOne o: Declare(o.Arg0); break;
                default: Declare(it); break;
            }
        }
        Walk(listItem);
    }

    /// <summary>Whether a declarator list declares a function (such a declaration is
    /// a prototype, which a re-included header repeats harmlessly, so it is never
    /// deduplicated as a header-defined variable is).</summary>
    private static bool DeclaresFunction(Item listItem) => listItem.Content switch
    {
        C.InitDeclaratorListCons c => DeclaresFunction(c.Arg0) || DeclaresFunction(c.Arg2),
        C.InitDeclaratorListOne o => DeclaresFunction(o.Arg0),
        C.InitDeclarator d => IsFunctionDeclarator(d.Arg0),
        C.InitDeclaratorInit d => IsFunctionDeclarator(d.Arg0),
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
            case C.BraceInit { Arg1: null }:
                Gate(2023, "empty initializer", init);
                return new DefaultLit { Type = type };
            case C.BraceInit { Arg1: { } list }:
                if (IsAggregateType(type)) { return BuildAggregateInit(type, list); }
                return BracedScalar(new InitGroup(ParseInitList(list)));
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
            case C.BraceInit { Arg1: null }:
                Gate(2023, "empty initializer", init);
                return new(arr, elem, CompleteFlatCount(d, arr), null);
            case C.BraceInit { Arg1: { } list }:
            {
                var items = ParseInitList(list);
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
            var extents = new List<CExpr>(vla.Count);
            foreach (var bound in vla) { extents.Add(BuildExpr(bound)); }
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
    /// lifetime (a <c>stackalloc</c> cannot). <paramref name="tuLocal"/> marks a
    /// file-scope one with internal linkage.</summary>
    private void BuildStaticArray(Declarator d, CType.Array arr, string? csName, bool tuLocal)
    {
        if (d.VlaDims is not null)
        {
            throw new IrUnsupportedException($"storage size of '{d.Name}' isn't constant");
        }
        // The array is in scope in its own initializer (C11 6.2.1p7: from the end of its
        // declarator), as parking_lot.c's `buckets[]` pointing into itself needs: declare it
        // first, then give it the size its initializer counts.
        var sym = DeclareGlobalArray(d.Name, arr, csName, tuLocal);
        var a = Owned(sym, () => ResolveArrayObject(d, arr));
        sym.Type = a.Type;
        var init = new PinnedArray(a.Elem, a.Elems, a.Elems is null ? CountLit(a.Count) : null) { Type = new CType.Pointer(a.Elem) };
        Globals.Add(new GlobalVar(sym, init));
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
        var synthetic = typeItem.Position.Line >= SrcPos.SyntheticLineBase;
        var pack = PackOfDefinition(typeItem);
        CType type;
        if (tag is not null)
        {
            BuildStructDef(tag, members, null, isUnion, synthetic, pack);
            type = new CType.Named(tag);
        }
        else if (hint is { } alias)
        {
            BuildStructDef(null, members, alias, isUnion, synthetic, pack);
            type = new CType.Named(alias);
        }
        else
        {
            type = ResolveAnonAggregate(members, isUnion, pack);
        }
        _tagDefs[typeItem] = type;
        return type;
    }

    /// <summary>The <c>#pragma pack</c> cap a struct or union definition was written
    /// under: the one <see cref="PragmaPackRewriter"/> stamped on its keyword, 0 for
    /// natural alignment.</summary>
    private static int PackOfDefinition(Item typeItem) => typeItem.Content switch
    {
        C.TypeStructDef t => PackedTagKeyword.Of(t.Arg0),
        C.TypeUnionDef t => PackedTagKeyword.Of(t.Arg0),
        _ => 0,
    };

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
    /// (defined or forward-declared) or enumeration constants. An untagged struct or
    /// union definition (a null tag) declares neither.</summary>
    private static bool DeclaresTag(Item typeItem) => typeItem.Content switch
    {
        C.TypeStruct or C.TypeUnion or C.TypeEnum
            or C.TypeStructDef { Arg1: not null } or C.TypeUnionDef { Arg1: not null }
            or C.EnumSpecifier => true,
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
        if (core.Content is C.TypeStructDef { Arg1: null } or C.TypeUnionDef { Arg1: null })
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
            // An untagged definition (a null tag) is a C11 anonymous member.
            case C.TypeStructDef { Arg1: null } a:
                Gate(2011, "anonymous struct/union member", member);
                AddAnonMember(a.Arg3, owner, fields, isUnion: false, PackedTagKeyword.Of(a.Arg0));
                return;
            case C.TypeUnionDef { Arg1: null } a:
                Gate(2011, "anonymous struct/union member", member);
                AddAnonMember(a.Arg3, owner, fields, isUnion: true, PackedTagKeyword.Of(a.Arg0));
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
        _anonTagHint = FirstPlainDeclarator(listItem);
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
        C.InitDeclaratorListCons c => FirstPlainDeclarator(c.Arg0),
        C.InitDeclaratorListOne o => FirstPlainDeclarator(o.Arg0),
        C.InitDeclarator { Arg0.Content: C.Declarator { Arg0: null, Arg1.Content: C.DeclaratorName n } } => Tok(n.Arg0),
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
