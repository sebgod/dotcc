#nullable enable

using System;
using System.Collections.Generic;
using Item = global::LALR.CC.LexicalGrammar.Item;

namespace DotCC.Ir;

/// <summary>Thrown when the IR builder meets a parse-tree node it doesn't yet
/// lower. Carries the node type name so the backend fails loudly on an
/// unsupported construct rather than silently miscompiling. A subclass of
/// <see cref="DotCC.CompileException"/> so callers catch the one public
/// compile-error type regardless of whether the cause was a parse error, an
/// invalid type, or an unsupported construct.</summary>
public sealed class IrUnsupportedException : DotCC.CompileException
{
    public IrUnsupportedException(string node) : base($"dotcc does not yet support: {node}") { }
}

/// <summary>
/// Builds the typed IR from the raw LALR parse tree (driven by
/// <see cref="ParseTreeIdentityVisitor"/>). A TOP-DOWN recursive walk with full
/// scope/type context — the opposite of the legacy bottom-up string emitter.
/// This is the sole backend: it covers the whole C surface dotcc supports.
/// A parse-tree node it doesn't yet lower raises
/// <see cref="IrUnsupportedException"/> (fail loudly, never silently miscompile).
/// </summary>
internal sealed partial class IrBuilder
{
    private readonly SymbolTable _symbols = new();
    // Typedef name → its underlying type. Unlike the legacy emitter (which emits
    // `using` aliases and resolves names textually), the IR resolves a typedef
    // name straight to its CType — so `size_t x` becomes `ulong x` with the right
    // SizeOf, no alias directive needed. Populated in declaration order, so a
    // chained typedef (`typedef size_t mysize;`) resolves through the table.
    private readonly Dictionary<string, CType> _typedefs = new(StringComparer.Ordinal);
    // Struct/union name → its fields, so member access can resolve a field's
    // type. Keyed by the canonical (tag, or anonymous-typedef alias) name.
    private readonly Dictionary<string, List<StructField>> _structFields = new(StringComparer.Ordinal);
    // Whether each registered aggregate is a union — drives the compile-time layout
    // model (offsetof folding) and is set alongside every _structFields entry.
    private readonly Dictionary<string, bool> _structIsUnion = new(StringComparer.Ordinal);
    // Enum tag → its resolved CType.Enum, so `enum Tag` as a type resolves to the
    // real enum (not plain int). Anonymous-but-typedef'd enums are reached through
    // _typedefs instead (the alias maps to the same CType.Enum).
    private readonly Dictionary<string, CType.Enum> _enumTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _emittedTypes = new(StringComparer.Ordinal);
    private string _file = "";
    // The name of the function currently being built — the value of the C99
    // predefined identifier `__func__` inside its body.
    private string _currentFnName = "";

    public List<FuncDef> Functions { get; } = new();
    public List<GlobalVar> Globals { get; } = new();
    public List<StructTypeDef> Types { get; } = new();
    public List<EnumTypeDef> Enums { get; } = new();
    public List<Diagnostic> Diagnostics { get; } = new();

    // Dialect-gating sink (the emit-pass half of -pedantic). Non-null only under
    // -pedantic / -pedantic-errors; the builder calls RequireMin at each construct
    // that postdates the selected -std=, mirroring the legacy emit-pass gate. A C
    // feature is structurally accepted by the one union grammar regardless of
    // dialect, so this is the rejection layer — and a pure no-op on the default path.
    private readonly DotCC.DialectGate? _gate;

    public IrBuilder(DotCC.DialectGate? gate = null) => _gate = gate;

    /// <summary>Flag a feature introduced in <paramref name="era"/> (ISO year) when
    /// the active dialect predates it. No-op when the gate is off or new enough.</summary>
    private void Gate(int era, string feature, Item it) => _gate?.RequireMin(era, feature, it.Position.Line);
    private void Gate(int era, string feature, SrcPos pos) => _gate?.RequireMin(era, feature, pos.Line);
    /// <summary>Gate a feature, then return an already-built value — for gating in
    /// expression-bodied switch arms (the value is built eagerly; gating only
    /// records, so evaluation order is irrelevant).</summary>
    private T Gated<T>(int era, string feature, Item it, T value) { Gate(era, feature, it); return value; }

    /// <summary>Walk one translation unit's parse tree, appending its functions
    /// / globals to the accumulated lists (file-scope symbols persist across
    /// units so a whole-program call resolves).</summary>
    public void AddUnit(Item root, string file)
    {
        _file = file;
        FlattenFns(root, BuildTopLevel);
    }

    // ---- top level -------------------------------------------------------

    private void FlattenFns(Item it, Action<Item> onFn)
    {
        // A translation unit with thousands of top-level declarations (Lua, with all
        // its #included prototypes) nests FnsCons thousands deep — recursing would
        // overflow dotcc's own stack. Flatten with an explicit stack (pushing the
        // right child first so the left is processed first), preserving source order.
        var stack = new Stack<Item>();
        stack.Push(it);
        var ordered = new List<Item>();
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            switch (n.Content)
            {
                case C.FnsCons c: stack.Push(c.Arg1); stack.Push(c.Arg0); break;
                case C.FnsOne o: stack.Push(o.Arg0); break;
                default: ordered.Add(n); break;
            }
        }
        foreach (var f in ordered) { onFn(f); }
    }

    private void BuildTopLevel(Item fn)
    {
        switch (fn.Content)
        {
            case C.FuncDef d: BuildFuncDef(d.Arg0, d.Arg1); break;
            case C.ExternFnDef d: BuildFuncDef(d.Arg1, d.Arg2); break;
            case C.FuncProto p: RegisterProto(p.Arg0); break;
            case C.ExternFnProto p: RegisterProto(p.Arg1); break;
            case C.GlobalDeclList g: BuildGlobalDecls(g.Arg0, g.Arg1, Storage.Static); break;
            case C.GlobalStaticDeclList g: BuildGlobalDecls(g.Arg1, g.Arg2, Storage.Static); break;
            // File-scope arrays — pinned global backing store (plain and `static`
            // lower identically; internal linkage is a no-op for a never-exported
            // variable). Sized, brace-initialized, and implicit-`[]` forms.
            case C.GlobalArr g: BuildGlobalArr(g.Arg0, g.Arg1, g.Arg2, null, null); break;
            case C.GlobalStaticArr g: BuildGlobalArr(g.Arg1, g.Arg2, g.Arg3, null, null); break;
            case C.GlobalArrInit g: BuildGlobalArr(g.Arg0, g.Arg1, g.Arg2, g.Arg5, null); break;
            case C.GlobalStaticArrInit g: BuildGlobalArr(g.Arg1, g.Arg2, g.Arg3, g.Arg6, null); break;
            case C.GlobalArrInitImplicit g: BuildGlobalArr(g.Arg0, g.Arg1, null, g.Arg6, null); break;
            case C.GlobalStaticArrInitImplicit g: BuildGlobalArr(g.Arg1, g.Arg2, null, g.Arg7, null); break;
            // File-scope char arrays from a string literal.
            case C.GlobalCharArrStr g: BuildGlobalCharArr(g.Arg0, g.Arg1, g.Arg5, null, null); break;
            case C.GlobalCharArrStrSized g: BuildGlobalCharArr(g.Arg0, g.Arg1, g.Arg4, g.Arg2, null); break;
            case C.GlobalStaticCharArrStr g: BuildGlobalCharArr(g.Arg1, g.Arg2, g.Arg6, null, null); break;
            case C.GlobalStaticCharArrStrSized g: BuildGlobalCharArr(g.Arg1, g.Arg2, g.Arg5, g.Arg3, null); break;
            // `extern T a[N];` / `extern T a[];` — declaration only (storage elsewhere).
            case C.ExternArr g: BuildExternArr(g.Arg1, g.Arg2, g.Arg3); break;
            case C.ExternArrIncomplete g: BuildExternArr(g.Arg1, g.Arg2, null); break;
            // `static T x = { … };` at file scope — a once-initialised struct/union field.
            case C.GlobalStaticStructInit g: BuildGlobalStructInit(g.Arg1, g.Arg2, g.Arg5); break;
            // `extern T x;` declares the name + type for resolution but emits no
            // field — the definition lives in another TU (dotcc whole-program model).
            case C.ExternVarDecl g: BuildGlobalDecls(g.Arg1, g.Arg2, Storage.Extern); break;
            // `typedef <type> <name>;` — record name → underlying type. Resolution
            // (ResolveType's TypeName case) then sees through it everywhere.
            case C.TypedefAlias t: _typedefs[Tok(t.Arg2)] = ResolveType(t.Arg1); break;
            // enum definitions — register a real C# enum (tagged/typedef'd) or, for
            // an anonymous un-typedef'd enum, plain int constants.
            case C.EnumDef e: RegisterEnum(Tok(e.Arg1), null, e.Arg3); break;
            case C.EnumDefTyped e: RegisterEnum(Tok(e.Arg1), e.Arg3, e.Arg5); break;
            case C.TypedefEnum e: _typedefs[Tok(e.Arg6)] = RegisterEnum(Tok(e.Arg2), null, e.Arg4, Tok(e.Arg6)); break;
            case C.TypedefEnumAnon e: _typedefs[Tok(e.Arg5)] = RegisterEnum(null, null, e.Arg3, Tok(e.Arg5)); break;
            // struct/union definitions.
            case C.StructDef s: BuildStructDef(Tok(s.Arg1), s.Arg3, null, isUnion: false); break;
            case C.UnionDef s: BuildStructDef(Tok(s.Arg1), s.Arg3, null, isUnion: true); break;
            case C.TypedefStruct s: BuildStructDef(Tok(s.Arg2), s.Arg4, Tok(s.Arg6), isUnion: false); break;
            case C.TypedefUnion s: BuildStructDef(Tok(s.Arg2), s.Arg4, Tok(s.Arg6), isUnion: true); break;
            case C.TypedefStructAnon s: BuildStructDef(null, s.Arg3, Tok(s.Arg5), isUnion: false); break;
            case C.TypedefUnionAnon s: BuildStructDef(null, s.Arg3, Tok(s.Arg5), isUnion: true); break;
            // `struct Tag;` forward declaration — C# resolves order-independently.
            case C.StructFwd: break;
            // `_Static_assert(expr[, "msg"]);` at file scope — a compile-time-only
            // assertion. A valid program's assertion holds, so dotcc emits nothing
            // (observably identical to gcc actually checking it). Both arities.
            case C.StaticAssert: case C.StaticAssertNoMsg: Gate(2011, "_Static_assert", fn); break;
            // `typedef Ret (*Name)(params);` — record Name → fn-ptr type.
            case C.TypedefFnPtr t: _typedefs[Tok(t.Arg4)] = FnPtrType(t.Arg1, t.Arg7); break;
            case C.TypedefFnPtrNoArgs t: _typedefs[Tok(t.Arg4)] = FnPtrType(t.Arg1, null); break;
            default: throw new IrUnsupportedException(TypeName(fn.Content));
        }
    }

    /// <summary>File-scope variable declaration. Each declarator becomes a
    /// <c>DotCcGlobals</c> field (codegen emits <c>public static unsafe T name</c>);
    /// an <c>extern</c> one is registered for resolution only (no field).</summary>
    private void BuildGlobalDecls(Item typeItem, Item listItem, Storage storage)
    {
        WalkDeclList(typeItem, listItem, (name, initItem, type) =>
        {
            var sym = _symbols.Declare(new Symbol
            {
                Name = name, Kind = SymKind.Var, Type = type, Storage = storage, IsGlobal = true,
            });
            if (storage != Storage.Extern)
            {
                Globals.Add(new GlobalVar(sym, initItem is { } ii ? BuildExpr(ii) : null));
            }
        });
    }

    /// <summary>A prototype declares the function (so calls resolve + we know its
    /// signature) but emits no body.</summary>
    private void RegisterProto(Item fnSig)
    {
        var sig = ExtractFnSig(fnSig);
        DeclareFunc(sig);
    }

    private void BuildFuncDef(Item fnSig, Item block)
    {
        var sig = ExtractFnSig(fnSig);
        var funcSym = DeclareFunc(sig);

        _symbols.BeginFunction();
        _currentFnName = sig.Name; // drives the `__func__` predefined identifier
        _symbols.EnterScope(); // parameter scope
        var paramSyms = new List<Symbol>(sig.Params.Count);
        foreach (var (pType, pName) in sig.Params)
        {
            paramSyms.Add(_symbols.Declare(new Symbol { Name = pName, Kind = SymKind.Param, Type = pType }));
        }
        var body = BuildBlock(block);
        _symbols.ExitScope();

        Functions.Add(new FuncDef(funcSym, paramSyms, body, sig.Variadic));
    }

    private Symbol DeclareFunc(FnSig sig)
    {
        var existing = _symbols.Resolve(sig.Name);
        if (existing is { Kind: SymKind.Func }) { return existing; } // re-declaration (proto then def)
        var paramTypes = new List<CType>(sig.Params.Count);
        foreach (var (t, _) in sig.Params) { paramTypes.Add(t); }
        return _symbols.Declare(new Symbol
        {
            Name = sig.Name,
            Kind = SymKind.Func,
            Type = new CType.Func(sig.Return, paramTypes, sig.Variadic),
            Storage = sig.IsStatic ? Storage.Static : Storage.None,
            IsGlobal = true,
        });
    }

    // ---- function signatures --------------------------------------------

    private readonly record struct FnSig(CType Return, string Name, List<ParamInfo> Params, bool Variadic, bool IsStatic);

    private FnSig ExtractFnSig(Item it) => it.Content switch
    {
        C.FnSig n => new(ResolveType(n.Arg0), Tok(n.Arg1), BuildParams(n.Arg3, out var v0), v0, false),
        C.FnSigNoArgs n => new(ResolveType(n.Arg0), Tok(n.Arg1), new(), false, false),
        C.FnSigVoidArgs n => new(ResolveType(n.Arg0), Tok(n.Arg1), new(), false, false),
        C.FnSigStatic n => new(ResolveType(n.Arg1), Tok(n.Arg2), BuildParams(n.Arg4, out var v1), v1, true),
        C.FnSigStaticNoArgs n => new(ResolveType(n.Arg1), Tok(n.Arg2), new(), false, true),
        C.FnSigStaticVoidArgs n => new(ResolveType(n.Arg1), Tok(n.Arg2), new(), false, true),
        // Parenthesized declarator name `T (name)(args)` — identical to
        // `T name(args)`; the parens are pure grouping around the name (public
        // headers wrap API names so a same-named function-like macro can't expand
        // at the declaration). Name is Arg2, the param list Arg5.
        C.FnSigParen n => new(ResolveType(n.Arg0), Tok(n.Arg2), BuildParams(n.Arg5, out var vp), vp, false),
        C.FnSigParenNoArgs n => new(ResolveType(n.Arg0), Tok(n.Arg2), new(), false, false),
        C.FnSigParenVoidArgs n => new(ResolveType(n.Arg0), Tok(n.Arg2), new(), false, false),
        // Function returning a function pointer: `Ret (*name(params))(fnPtrParams)`
        // (e.g. <signal.h>'s `void (*signal(int, void(*)(int)))(int)`). The result
        // type is the function-pointer `Ret (*)(fnPtrParams)`; name + params are the
        // outer declarator's.
        C.FnSigRetFnPtr n => new(FnPtrType(n.Arg0, n.Arg9), Tok(n.Arg3), BuildParams(n.Arg5, out var vr), vr, false),
        C.FnSigRetFnPtrNoArgs n => new(FnPtrType(n.Arg0, null), Tok(n.Arg3), BuildParams(n.Arg5, out var vrn), vrn, false),
        C.FnSigRetFnPtrVoid n => new(FnPtrType(n.Arg0, null), Tok(n.Arg3), BuildParams(n.Arg5, out var vrv), vrv, false),
        _ => throw new IrUnsupportedException(TypeName(it.Content)),
    };

    private List<ParamInfo> BuildParams(Item paramList, out bool variadic)
    {
        var acc = new List<ParamInfo>();
        var vararg = false;
        var unnamed = 0;
        void Walk(Item it)
        {
            switch (it.Content)
            {
                case C.ParamsCons c: Walk(c.Arg0); Walk(c.Arg2); break;
                case C.ParamsOne o: Walk(o.Arg0); break;
                case C.ParamsVararg v: Walk(v.Arg0); vararg = true; break;
                case C.Param p: acc.Add(new(ResolveType(p.Arg0), Tok(p.Arg1))); break;
                case C.ParamUnnamed p: acc.Add(new(ResolveType(p.Arg0), "_p" + unnamed++)); break;
                case C.ParamArrayUnsized p: acc.Add(new(new CType.Pointer(ResolveType(p.Arg0)), Tok(p.Arg1))); break;
                case C.ParamArraySized p: acc.Add(new(new CType.Pointer(ResolveType(p.Arg0)), Tok(p.Arg1))); break;
                // Function-pointer parameter: `Ret (*name)(paramTypes)`.
                case C.ParamFnPtr p: acc.Add(new(FnPtrType(p.Arg0, p.Arg6), Tok(p.Arg3))); break;
                case C.ParamFnPtrNoArgs p: acc.Add(new(FnPtrType(p.Arg0, null), Tok(p.Arg3))); break;
                default: throw new IrUnsupportedException(TypeName(it.Content));
            }
        }
        Walk(paramList);
        variadic = vararg;
        return acc;
    }

    // ---- enums -----------------------------------------------------------

    /// <summary>Register an enum definition. Enumerator values auto-increment from
    /// the previous (starting at 0), with an explicit <c>= expr</c> overriding (a
    /// constant integer expression). A TAGGED or TYPEDEF-named enum becomes a real
    /// C# enum: each enumerator is a <see cref="SymKind.EnumConst"/> typed AS the
    /// enum (so it renders <c>EnumName.Member</c> and decays to its underlying int
    /// only at C's plain-int contexts), and an <see cref="EnumTypeDef"/> is emitted.
    /// An ANONYMOUS, un-typedef'd enum has no C# name, so its enumerators stay plain
    /// int constants (named constants) rather than synthesizing a type. Returns the
    /// enum's <see cref="CType"/> — the <see cref="CType.Enum"/>, or
    /// <see cref="CType.Int"/> for the anonymous form — so a typedef caller can alias
    /// the name to it.</summary>
    private CType RegisterEnum(string? tag, Item? baseType, Item enumList, string? typedefName = null)
    {
        var underlying = baseType is { } bt ? ResolveType(bt) : CType.Int;
        if (baseType is { } baseItem) { Gate(2023, "enum with a fixed underlying type", baseItem); }
        // The C# enum name: the tag, else the typedef alias; anonymous + un-typedef'd
        // ⇒ null ⇒ plain int constants.
        var enumName = tag ?? typedefName;
        CType.Enum? enumType = enumName is null ? null : new CType.Enum(enumName, underlying);
        var members = new List<EnumMember>();
        long next = 0;
        void Walk(Item it)
        {
            switch (it.Content)
            {
                case C.EnumListCons c: Walk(c.Arg0); Walk(c.Arg2); break;
                case C.EnumListOne o: Walk(o.Arg0); break;
                case C.EnumItem e: Add(Tok(e.Arg0), null); break;
                case C.EnumItemInit e: Add(Tok(e.Arg0), e.Arg2); break;
                default: throw new IrUnsupportedException(TypeName(it.Content));
            }
        }
        void Add(string name, Item? valueItem)
        {
            if (valueItem is { } vi)
            {
                next = ConstEval(BuildExpr(vi))
                    ?? throw new IrUnsupportedException("non-constant enum initializer for " + name);
            }
            _symbols.Declare(new Symbol
            {
                Name = name, Kind = SymKind.EnumConst,
                Type = (CType?)enumType ?? CType.Int, ConstValue = next, IsGlobal = true,
            });
            if (enumType is not null) { members.Add(new EnumMember(name, next)); }
            next++;
        }
        Walk(enumList);
        if (enumType is not null)
        {
            if (tag is not null) { _enumTypes[tag] = enumType; }
            if (Enums.All(e => e.Name != enumName)) { Enums.Add(new EnumTypeDef(enumName!, underlying, members)); }
        }
        return (CType?)enumType ?? CType.Int;
    }

    /// <summary>Evaluate an integer constant expression, or null if non-constant.
    /// Covers the forms enum initializers and array bounds use (literals,
    /// unary +/-/~, the binary arithmetic/shift/bitwise operators, parens,
    /// integer casts, <c>sizeof</c>, <c>offsetof</c>, and already-resolved
    /// enum-constant literals). <c>sizeof</c>/<c>offsetof</c> of a user aggregate use
    /// the compile-time layout model so they can size an array bound.</summary>
    private long? ConstEval(CExpr e) => e switch
    {
        LitInt i => i.Value,
        EnumConstRef ec => ec.Sym.ConstValue,
        SizeOfExpr s => SizeOfConst(s.Of),
        OffsetOf o => StructCanonical(o.StructType) is { } n ? OffsetOfConst(n, o.Member) : null,
        Paren p => ConstEval(p.Inner),
        Cast c => ConstEval(c.Operand),
        Unary u => u.Op switch
        {
            UnOp.Plus => ConstEval(u.Operand),
            UnOp.Neg => ConstEval(u.Operand) is { } v ? -v : null,
            UnOp.BitNot => ConstEval(u.Operand) is { } v ? ~v : null,
            _ => null,
        },
        Binary b => ConstEval(b.Left) is { } l && ConstEval(b.Right) is { } r ? ApplyConstBin(b.Op, l, r) : null,
        _ => null,
    };

    /// <summary>The constant byte size of a type — the layout model for a user
    /// aggregate (so the size is exact for an array bound), else the type's own
    /// <see cref="CType.SizeOf"/>.</summary>
    private long? SizeOfConst(CType t) =>
        t.Unqualified is CType.Named n && _structFields.ContainsKey(n.Name) ? Layout(t).Size : t.SizeOf;

    private static long? ApplyConstBin(BinOp op, long l, long r) => op switch
    {
        BinOp.Add => l + r, BinOp.Sub => l - r, BinOp.Mul => l * r,
        BinOp.Div => r != 0 ? l / r : null, BinOp.Mod => r != 0 ? l % r : null,
        BinOp.Shl => l << (int)r, BinOp.Shr => l >> (int)r,
        BinOp.BitAnd => l & r, BinOp.BitOr => l | r, BinOp.BitXor => l ^ r,
        _ => null,
    };

    // ---- compile-time C-ABI layout (for offsetof / sizeof folding) --------
    // The .NET blittable layout dotcc emits (sequential structs, explicit unions,
    // natural alignment on this LP64 target) matches the C ABI for the types it
    // models, so size/offset can be computed at compile time — which an array bound
    // like `char padding[offsetof(T, m)]` requires (Lua's alignment-union trick).

    /// <summary>The (size, alignment) in bytes of a type under the C ABI / .NET
    /// blittable layout.</summary>
    private (int Size, int Align) Layout(CType t)
    {
        switch (t.Unqualified)
        {
            case CType.Prim p: return (p.Bytes, p.Bytes);
            case CType.Pointer or CType.Func: return (8, 8);
            case CType.Array a:
            {
                var (es, ea) = Layout(a.FlatElement);
                var count = 1;
                for (CType c = a; c is CType.Array ca; c = ca.Element) { count *= ca.Count ?? 0; }
                return (es * count, ea);
            }
            case CType.Named n: return LayoutAggregate(n.Name);
            default: return (0, 1);
        }
    }

    /// <summary>The (size, alignment) of a registered struct/union: sequential
    /// fields each aligned up to their own alignment for a struct; all overlaid at 0
    /// for a union. The total rounds up to the aggregate's alignment.</summary>
    private (int Size, int Align) LayoutAggregate(string name)
    {
        if (!_structFields.TryGetValue(name, out var fields)) { return (0, 1); } // opaque/unknown
        var isUnion = _structIsUnion.GetValueOrDefault(name);
        int align = 1, size = 0, off = 0;
        foreach (var f in fields)
        {
            var (fs, fa) = Layout(f.Type);
            if (fa > align) { align = fa; }
            if (isUnion) { if (fs > size) { size = fs; } }
            else { off = RoundUp(off, fa) + fs; }
        }
        return (RoundUp(isUnion ? size : off, align), align);
    }

    /// <summary>The byte offset of <paramref name="member"/> within struct
    /// <paramref name="structName"/> (0 for any union member), or null if unknown.</summary>
    private int? OffsetOfConst(string structName, string member)
    {
        if (!_structFields.TryGetValue(structName, out var fields)) { return null; }
        if (_structIsUnion.GetValueOrDefault(structName)) { return 0; }
        var off = 0;
        foreach (var f in fields)
        {
            var (fs, fa) = Layout(f.Type);
            off = RoundUp(off, fa);
            if (f.Name == member) { return off; }
            off += fs;
        }
        return null;
    }

    private static int RoundUp(int v, int align) => align <= 1 ? v : (v + align - 1) / align * align;

    // ---- structs / unions ------------------------------------------------

    /// <summary>Define a struct/union. <paramref name="tag"/> is the C tag (null
    /// for an anonymous <c>typedef struct {…} Alias</c>); <paramref name="alias"/>
    /// the typedef name if any. Emits a C# struct under a canonical name, records
    /// its fields for member-type resolution, and maps the typedef alias to it.</summary>
    private void BuildStructDef(string? tag, Item memberList, string? alias, bool isUnion)
    {
        var canonical = tag ?? alias ?? throw new IrUnsupportedException("struct with neither tag nor typedef name");
        var fields = BuildStructFields(memberList, canonical);
        if (_emittedTypes.Add(canonical))
        {
            _structFields[canonical] = fields;
            _structIsUnion[canonical] = isUnion;
            Types.Add(new StructTypeDef(canonical, fields, isUnion));
        }
        // `struct Tag` and the typedef alias both resolve to the canonical type.
        if (alias is not null) { _typedefs[alias] = new CType.Named(canonical); }
    }

    /// <summary>Promoted (anonymous-aggregate) field map: per owning struct/union,
    /// each promoted inner field name → (the hidden container field, the nested
    /// aggregate's type name). A C11 anonymous <c>struct {…};</c> / <c>union {…};</c>
    /// member lifts its fields into the parent — dotcc keeps them in a generated
    /// nested type and rewrites <c>v.i</c> to <c>v.hidden.i</c> at access time.</summary>
    private readonly Dictionary<string, Dictionary<string, (string Hidden, string Nested)>> _promoted = new(StringComparer.Ordinal);

    /// <summary>Parse a struct/union member list into <see cref="StructField"/>s for
    /// the <paramref name="owner"/> aggregate. Scalar/pointer, array (incl.
    /// multi-dim / flexible / non-primitive), bit-field, and C11 anonymous
    /// struct/union members are supported.</summary>
    private List<StructField> BuildStructFields(Item memberList, string owner)
    {
        var fields = new List<StructField>();
        void Member(Item m)
        {
            switch (m.Content)
            {
                case C.MembersCons c: Member(c.Arg0); Member(c.Arg1); break;
                case C.MembersOne o: Member(o.Arg0); break;
                case C.StructMemberList sm:
                    WalkDeclList(sm.Arg0, sm.Arg1, (name, _, type) => fields.Add(new StructField(name, type)));
                    break;
                // C11 anonymous struct/union member — its fields are promoted into
                // the parent. Held in a generated nested aggregate + a hidden field;
                // each inner name is recorded so `parent.inner` routes through it.
                case C.AnonStructMember am: Gate(2011, "anonymous struct/union member", m); AddAnonMember(am.Arg3, owner, fields, isUnion: false); break;
                case C.AnonUnionMember am: Gate(2011, "anonymous struct/union member", m); AddAnonMember(am.Arg3, owner, fields, isUnion: true); break;
                // A NAMED member of a nested aggregate type — `struct {…} m;` /
                // `struct Tag {…} m;` (and union forms). Define the (tagged or
                // synthesized) type, then add `m` of that type — no promotion.
                case C.NamedNestedStruct nm: AddNamedNested(null, nm.Arg3, Tok(nm.Arg5), fields, isUnion: false); break;
                case C.NamedNestedUnion nm: AddNamedNested(null, nm.Arg3, Tok(nm.Arg5), fields, isUnion: true); break;
                case C.NamedNestedTaggedStruct nm: AddNamedNested(Tok(nm.Arg1), nm.Arg4, Tok(nm.Arg6), fields, isUnion: false); break;
                case C.NamedNestedTaggedUnion nm: AddNamedNested(Tok(nm.Arg1), nm.Arg4, Tok(nm.Arg6), fields, isUnion: true); break;
                // `T name[N]…;` — a fixed-size array member (codegen: a `fixed`
                // buffer for a primitive element, an [InlineArray] wrapper for a
                // non-primitive one). Multi-dimensional bounds give a nested array
                // type so `s.m[i][j]` strides; bounds must be constant expressions.
                case C.StructArrMember sm:
                {
                    var dims = TryConstDims(sm.Arg2) ?? throw new IrUnsupportedException("non-constant struct array bound");
                    fields.Add(new StructField(Tok(sm.Arg1), MakeArrayType(ResolveType(sm.Arg0), dims)));
                    break;
                }
                // C99 flexible array member `T name[];` — over-allocated at malloc
                // time. Model as a 1-element array (the struct-hack [1] convention),
                // so the member exists and access over-indexes into the tail.
                case C.StructFlexArrMember sm:
                    Gate(1999, "flexible array member", m);
                    fields.Add(new StructField(Tok(sm.Arg1), new CType.Array(ResolveType(sm.Arg0), 1)));
                    break;
                // `T name : W;` — a bit-field. Codegen lowers it to a backing field
                // + a masked accessor property (value semantics; bit packing is
                // implementation-defined, so the struct's layout/sizeof needn't match).
                case C.StructBitField sm:
                {
                    var w = ConstEval(BuildExpr(sm.Arg3)) ?? throw new IrUnsupportedException("non-constant bit-field width");
                    fields.Add(new StructField(Tok(sm.Arg1), ResolveType(sm.Arg0), (int)w));
                    break;
                }
                // `T : W;` — an anonymous bit-field (padding). No accessible member;
                // dotcc's value-only bit-field model drops it.
                case C.StructAnonBitField:
                    break;
                default: throw new IrUnsupportedException(TypeName(m.Content));
            }
        }
        Member(memberList);
        return fields;
    }

    /// <summary>Add a C11 anonymous struct/union member: build it as a generated
    /// nested aggregate type, add a hidden container field to the parent, and record
    /// each inner field as promoted (so <c>parent.inner</c> rewrites to
    /// <c>parent.hidden.inner</c> at access time, keeping the union's overlap /
    /// the struct's sequential layout).</summary>
    private void AddAnonMember(Item innerMemberList, string owner, List<StructField> parentFields, bool isUnion)
    {
        var nested = $"__Anon{_anonAggrSeq++}";
        var innerFields = BuildStructFields(innerMemberList, nested);
        _structFields[nested] = innerFields;
        _structIsUnion[nested] = isUnion;
        Types.Add(new StructTypeDef(nested, innerFields, isUnion));

        var hidden = "__anon_" + nested;
        parentFields.Add(new StructField(hidden, new CType.Named(nested)));

        if (!_promoted.TryGetValue(owner, out var pm)) { _promoted[owner] = pm = new(StringComparer.Ordinal); }
        foreach (var f in innerFields) { pm[f.Name] = (hidden, nested); }
    }

    /// <summary>Add a NAMED member of a nested aggregate type (<c>struct {…} m;</c>
    /// or a tagged <c>struct Tag {…} m;</c>, and union forms). Defines the nested
    /// type (under its tag, or a synthesized name) and adds <paramref name="member"/>
    /// of that type — unlike an anonymous member, the fields are NOT promoted.</summary>
    private void AddNamedNested(string? tag, Item innerMemberList, string member, List<StructField> parentFields, bool isUnion)
    {
        var typeName = tag ?? $"__Anon{_anonAggrSeq++}";
        if (_emittedTypes.Add(typeName))
        {
            var inner = BuildStructFields(innerMemberList, typeName);
            _structFields[typeName] = inner;
            _structIsUnion[typeName] = isUnion;
            Types.Add(new StructTypeDef(typeName, inner, isUnion));
        }
        parentFields.Add(new StructField(member, new CType.Named(typeName)));
    }

    /// <summary>The CType of <paramref name="field"/> read off the struct/union
    /// that <paramref name="baseExpr"/>'s type names (pointer levels peeled), or
    /// <see cref="CType.Int"/> when unknown (e.g. an as-yet-unregistered struct).</summary>
    private CType MemberType(CExpr baseExpr, string field)
    {
        var t = baseExpr.Type;
        while (t is CType.Pointer p) { t = p.Pointee; }
        if (t is CType.Named n && _structFields.TryGetValue(n.Name, out var fields))
        {
            foreach (var f in fields) { if (f.Name == field) { return f.Type; } }
        }
        return CType.Int;
    }

    /// <summary>Build a function-pointer type <c>Ret (*)(params)</c> →
    /// <see cref="CType.Func"/> (codegen lowers it to <c>delegate*&lt;params, Ret&gt;</c>).
    /// A lone <c>void</c> parameter list means no parameters.</summary>
    private CType.Func FnPtrType(Item retItem, Item? paramListItem)
    {
        var ret = ResolveType(retItem);
        var ptypes = new List<CType>();
        var variadic = false;
        if (paramListItem is { } pl)
        {
            var ps = BuildParams(pl, out variadic);
            if (!(ps.Count == 1 && ps[0].Type is CType.VoidType))
            {
                foreach (var p in ps) { ptypes.Add(p.Type); }
            }
        }
        return new CType.Func(ret, ptypes, variadic);
    }

    // ---- types -----------------------------------------------------------

    private CType ResolveType(Item it) => it.Content switch
    {
        C.TypeFromSpec t => ResolveSpecs(CollectSpecs(t.Arg0), SrcPos.From(it)),
        C.TypePtr t => new CType.Pointer(ResolveType(t.Arg0)),
        C.TypePtrQualConst t => new CType.Pointer(ResolveType(t.Arg0)),
        C.TypePtrQualVolatile t => new CType.Pointer(ResolveType(t.Arg0)),
        // `volatile T` — leading qualifier prefix. Carry the flag on the type;
        // codegen fences reads/writes of a volatile scalar lvalue (Volatile.Read/Write).
        C.TypeVolatile t => ResolveType(t.Arg1).WithQuals(TypeQual.Volatile),
        // `_Atomic T` / `_Atomic(T)` (C11). Codegen lowers reads/writes of an atomic
        // scalar lvalue to seq-cst Atomic.Load/Store/*Fetch (Interlocked-backed).
        C.TypeAtomic t => AtomicType(ResolveType(t.Arg1), it),
        C.TypeAtomicParen t => AtomicType(ResolveType(t.Arg2), it),
        C.TypePtrQualRestrict t => new CType.Pointer(ResolveType(t.Arg0)),
        C.TypeName t => ResolveTypeName(Tok(t.Arg0)),
        // `enum Tag` as a type — the registered real C# enum, or plain int if the
        // tag is unknown (forward/opaque) or names an anonymous int-constant enum.
        C.TypeEnum te => _enumTypes.TryGetValue(Tok(te.Arg1), out var et) ? et : CType.Int,
        // `struct Tag` / `union Tag` as a type — the canonical C# struct name.
        C.TypeStruct t => new CType.Named(Tok(t.Arg1)),
        C.TypeUnion t => new CType.Named(Tok(t.Arg1)),
        // Inline anonymous aggregate used as a type — `union { int i; float f; } u;`
        // (a NAMED member/var of an unnamed aggregate). Synthesize a struct name.
        C.TypeAnonStruct t => ResolveAnonAggregate(it, t.Arg3, isUnion: false),
        C.TypeAnonUnion t => ResolveAnonAggregate(it, t.Arg3, isUnion: true),
        // `TypeSpecList TYPE_NAME` — a function-specifier run (inline / _Noreturn)
        // immediately preceding a typedef-name: `static inline Cell *bump(…)`,
        // Lua's `l_sinline Table *gettable(…)`. The spec list contributes only the
        // inline/_Noreturn flag, which the IR drops (it emits no MethodImpl hint —
        // exactly as every other inline function is lowered), so the TYPE_NAME is
        // the whole base type.
        C.TypeSpecThenName t => ResolveTypeName(Tok(t.Arg1)),
        // C23 `typeof(expr)` / `typeof(type)` — the expr form reads the operand's
        // synthesized CType (qualifiers dropped, as `typeof_unqual` does); the type
        // form unwraps to that type. The expr isn't evaluated (only its type taken).
        C.TypeofExpr t => BuildExpr(t.Arg2).Type.Unqualified,
        C.TypeofType t => ResolveType(t.Arg2),
        // A function-pointer type `Ret (*)(params)` — abstract declarator (a cast
        // target, a typedef target, a sizeof operand): lowers to CType.Func.
        C.TypeFnPtr t => FnPtrType(t.Arg0, t.Arg5),
        C.TypeFnPtrNoArgs t => FnPtrType(t.Arg0, null),
        C.TypeFnPtrVoid t => FnPtrType(t.Arg0, null),
        _ => throw new IrUnsupportedException(TypeName(it.Content)),
    };

    /// <summary>Per-occurrence synthetic name for an inline anonymous aggregate
    /// type, cached by source position so the same type item always resolves to
    /// the same <see cref="CType.Named"/> (the field that holds it and every
    /// member access agree on one synthesized struct).</summary>
    private readonly Dictionary<SrcPos, CType> _anonAggregates = new();
    private int _anonAggrSeq;

    private CType ResolveAnonAggregate(Item typeItem, Item memberListItem, bool isUnion)
    {
        var pos = SrcPos.From(typeItem);
        if (_anonAggregates.TryGetValue(pos, out var cached)) { return cached; }
        var name = $"__Anon{_anonAggrSeq++}";
        var named = new CType.Named(name);
        _anonAggregates[pos] = named;
        var fields = BuildStructFields(memberListItem, name);
        _structFields[name] = fields;
        _structIsUnion[name] = isUnion;
        Types.Add(new StructTypeDef(name, fields, isUnion));
        return named;
    }

    /// <summary>Resolve a type-name token: a user/library typedef resolves to its
    /// underlying type; an unknown name (a predefined opaque type like
    /// <c>FILE</c> / <c>jmp_buf</c>'s target) stays a <see cref="CType.Named"/>
    /// whose <c>CsType</c> is the name itself.</summary>
    private CType ResolveTypeName(string name) =>
        _typedefs.TryGetValue(name, out var t) ? t : new CType.Named(name);

    private List<string> CollectSpecs(Item it)
    {
        var acc = new List<string>();
        void Walk(Item node)
        {
            switch (node.Content)
            {
                case C.TypeSpecListCons c: Walk(c.Arg0); Walk(c.Arg1); break;
                case C.TypeSpecListOne o: Walk(o.Arg0); break;
                default: acc.Add(SpecKeyword(node.Content)); break;
            }
        }
        Walk(it);
        return acc;
    }

    private static string SpecKeyword(object spec) => spec switch
    {
        C.TsInt => "int",
        C.TsChar => "char",
        C.TsFloat => "float",
        C.TsDouble => "double",
        C.TsVoid => "void",
        C.TsShort => "short",
        C.TsLong => "long",
        C.TsUnsigned => "unsigned",
        C.TsSigned => "signed",
        C.TsBool => "_Bool",
        C.TsFloat128 => "Float128",
        C.TsConst => "const",
        C.TsInline => "inline",
        C.TsNoreturn => "_Noreturn",
        C.TsComplex => "_Complex",
        _ => throw new IrUnsupportedException(TypeName(spec)),
    };

    /// <summary>Resolve a declaration-specifier multiset to a <see cref="CType"/>.
    /// Order-insensitive; covers the slice (int/char/short/long/long long with
    /// signedness, float/double/long double, void, _Bool). <c>const</c> sets the
    /// qualifier (though the legacy QualifierStripper removes it pre-parse today).</summary>
    /// <summary>Apply the <c>_Atomic</c> qualifier (C11), flagging it under an
    /// older -std=.</summary>
    private CType AtomicType(CType inner, Item it)
    {
        Gate(2011, "_Atomic", it);
        return inner.WithQuals(TypeQual.Atomic);
    }

    private CType ResolveSpecs(List<string> specs, SrcPos pos)
    {
        int u = 0, s = 0, sh = 0, lng = 0, baseCount = 0;
        var quals = TypeQual.None;
        var isComplex = false;
        string? base_ = null;
        foreach (var k in specs)
        {
            switch (k)
            {
                case "unsigned": u++; break;
                case "signed": s++; break;
                case "short": sh++; break;
                case "long": lng++; break;
                case "const": quals |= TypeQual.Const; break;
                // C99 _Complex — every width widens to the double-backed Complex.
                case "_Complex": isComplex = true; break;
                case "inline" or "_Noreturn": break; // ignored for type purposes here
                case "void": base_ = "void"; baseCount++; break;
                case "char": base_ = "char"; baseCount++; break;
                case "int": base_ = "int"; baseCount++; break;
                case "float": base_ = "float"; baseCount++; break;
                case "double": base_ = "double"; baseCount++; break;
                case "_Bool": base_ = "_Bool"; baseCount++; break;
                case "Float128": base_ = "Float128"; baseCount++; break;
            }
        }

        // Reject the ill-formed specifier multisets the C standard forbids (the
        // legacy emitter diagnosed these; the messages match its/clang's intent).
        // A purely permissive resolver would silently mis-type `long long long` or
        // `short double`. Order matters only for which message a multi-error combo
        // reports first.
        if (u >= 1 && s >= 1) { throw new DotCC.CompileException("cannot combine `signed` and `unsigned`"); }
        if (u >= 2) { throw new DotCC.CompileException("duplicate `unsigned`"); }
        if (s >= 2) { throw new DotCC.CompileException("duplicate `signed`"); }
        if (sh >= 1 && lng >= 1) { throw new DotCC.CompileException("cannot combine `short` and `long`"); }
        if (sh >= 2) { throw new DotCC.CompileException("duplicate `short`"); }
        if (lng >= 3) { throw new DotCC.CompileException("more than two `long` in a type"); }
        if (baseCount >= 2) { throw new DotCC.CompileException("multiple base types in a declaration"); }
        if (base_ == "_Bool" && (u > 0 || s > 0 || sh > 0 || lng > 0 || isComplex))
        { throw new DotCC.CompileException("`_Bool` cannot be combined with other type specifiers"); }
        if (base_ == "Float128" && (u > 0 || s > 0 || sh > 0 || lng > 0 || isComplex))
        { throw new DotCC.CompileException("`_Float128` cannot be combined with other type specifiers"); }
        if (base_ == "float" && (u > 0 || s > 0 || sh > 0 || lng > 0))
        { throw new DotCC.CompileException("`float` cannot take size or sign modifiers"); }
        if (base_ == "double" && lng >= 2)
        { throw new DotCC.CompileException("`long long double` is not a valid type"); }
        if (base_ == "double" && (u > 0 || s > 0 || sh > 0))
        { throw new DotCC.CompileException("`double` cannot take sign or `short` modifiers"); }
        if (isComplex && base_ is not ("float" or "double"))
        { throw new DotCC.CompileException("`_Complex` requires a `float`, `double`, or `long double` base"); }

        // Dialect gates: type-spec features newer than the selected -std=.
        if (specs.Contains("_Noreturn")) { Gate(2011, "_Noreturn", pos); }
        if (base_ == "_Bool") { Gate(1999, "_Bool", pos); }
        if (lng >= 2) { Gate(1999, "long long", pos); }
        if (isComplex) { Gate(1999, "_Complex", pos); }

        CType t = base_ switch
        {
            "void" => CType.Void,
            "_Bool" => CType.Bool,
            "Float128" => new CType.Named("Float128"),
            "float" => CType.Float,
            "double" => lng >= 1 ? CType.LongDouble : CType.Double,
            "char" => u > 0 ? CType.UChar : s > 0 ? CType.SChar : CType.Char,
            _ => sh > 0 ? (u > 0 ? CType.UShort : CType.Short)
                 : lng >= 2 ? (u > 0 ? CType.ULongLong : CType.LongLong)
                 : lng == 1 ? (u > 0 ? CType.ULong : CType.Long)
                 : (u > 0 ? CType.UInt : CType.Int),
        };
        if (isComplex) { return CType.Complex.WithQuals(quals); }
        return t.WithQuals(quals);
    }

    // ---- statements ------------------------------------------------------

    private Block BuildBlock(Item it)
    {
        var stmts = new List<CStmt>();
        switch (it.Content)
        {
            case C.Block b:
                _symbols.EnterScope();
                // C90 requires all declarations to precede statements in a block; a
                // declaration after a statement ("mixed declarations") is C99+.
                var sawNonDecl = false;
                FlattenStmts(b.Arg2, x =>
                {
                    if (x.Content is C.StmtDecl) { if (sawNonDecl) { Gate(1999, "mixed declarations and code", x); } }
                    else { sawNonDecl = true; }
                    stmts.Add(BuildStmt(x));
                });
                _symbols.ExitScope();
                break;
            case C.BlockEmpty:
                break;
            default:
                throw new IrUnsupportedException(TypeName(it.Content));
        }
        return new Block(stmts);
    }

    /// <summary>A statement that emits nothing (a hoisted local type definition,
    /// a bare <c>;</c>). An empty block is dropped wholesale by codegen.</summary>
    private static CStmt EmptyStmt(SrcPos pos) => new Block(System.Array.Empty<CStmt>()) { Pos = pos };

    private void FlattenStmts(Item it, Action<Item> onStmt)
    {
        // Iterative walk of the statement list (a large function body — Lua's
        // luaV_execute — would otherwise recurse one frame per statement).
        while (true)
        {
            switch (it.Content)
            {
                case C.StmtsCons c: onStmt(c.Arg0); it = c.Arg1; continue;
                case C.StmtsOne o: onStmt(o.Arg0); break;
                default: onStmt(it); break;
            }
            break;
        }
    }

    private CStmt BuildStmt(Item it)
    {
        var pos = SrcPos.From(it);
        switch (it.Content)
        {
            case C.Block: return BuildBlock(it);
            case C.BlockEmpty: return BuildBlock(it);
            case C.StmtDecl d: return BuildDeclStmt(d.Arg0) with { Pos = pos };
            case C.StmtStaticDecl s: return BuildStmtStaticDecl(s) with { Pos = pos };
            case C.StmtStaticStructInit s: return BuildStmtStaticStructInit(s) with { Pos = pos };
            // Block-scope `static` arrays — pinned global storage under a mangled
            // name (same static storage duration as a file-scope array).
            case C.StmtStaticArr s: return BuildStaticLocalArr(s.Arg1, s.Arg2, s.Arg3, null) with { Pos = pos };
            case C.StmtStaticArrInit s: return BuildStaticLocalArr(s.Arg1, s.Arg2, s.Arg3, s.Arg6) with { Pos = pos };
            case C.StmtStaticArrInitImplicit s: return BuildStaticLocalArr(s.Arg1, s.Arg2, null, s.Arg7) with { Pos = pos };
            // Block-scope `static char a[] = "…"` / sized — pinned global char array.
            case C.StmtStaticCharArrStr s: return BuildStaticLocalCharArr(s.Arg1, s.Arg2, s.Arg6, null) with { Pos = pos };
            case C.StmtStaticCharArrStrSized s: return BuildStaticLocalCharArr(s.Arg1, s.Arg2, s.Arg5, s.Arg3) with { Pos = pos };
            // Block-scope aggregate TYPE definitions (`struct cD { … };` inside a
            // function body — the block-scope enum forms are handled below). A
            // type has no storage, so C allows this; dotcc hoists the definition
            // into the top-level type section (deduped by tag, exactly as a
            // file-scope definition) and the statement emits nothing.
            case C.StmtStructDef s: BuildStructDef(Tok(s.Arg1), s.Arg3, null, isUnion: false); return EmptyStmt(pos);
            case C.StmtUnionDef s: BuildStructDef(Tok(s.Arg1), s.Arg3, null, isUnion: true); return EmptyStmt(pos);
            // Block-scope `_Static_assert(expr[, "msg"]);` — compile-time only,
            // emits nothing (same as the file-scope forms). Both arities.
            case C.StaticAssertStmt: case C.StaticAssertStmtNoMsg: return EmptyStmt(pos);
            case C.StmtExpr e: return new ExprStmt(BuildExpr(e.Arg0)) { Pos = pos };
            case C.StmtEmpty: return new Block(System.Array.Empty<CStmt>()) { Pos = pos };
            case C.StmtIf s:
            {
                var cond = BuildExpr(s.Arg2);
                var then = BuildStmt(s.Arg4);
                return SetjmpGuardOf(cond, then, null, pos) ?? new If(cond, then, null) { Pos = pos };
            }
            case C.StmtIfElse s:
            {
                var cond = BuildExpr(s.Arg2);
                var then = BuildStmt(s.Arg4);
                var els = BuildStmt(s.Arg6);
                return SetjmpGuardOf(cond, then, els, pos) ?? new If(cond, then, els) { Pos = pos };
            }
            case C.StmtWhile s: return new While(BuildExpr(s.Arg2), BuildStmt(s.Arg4)) { Pos = pos };
            case C.StmtDoWhile s: return new DoWhile(BuildStmt(s.Arg1), BuildExpr(s.Arg4)) { Pos = pos };
            case C.StmtReturn s: return new Return(BuildExpr(s.Arg1)) { Pos = pos };
            case C.StmtReturnVoid: return new Return(null) { Pos = pos };
            case C.StmtBreak: return new Break { Pos = pos };
            case C.StmtContinue: return new Continue { Pos = pos };
            case C.StmtForDecl s: return BuildForDecl(s) with { Pos = pos };
            case C.StmtForExpr s:
                return new For(new ExprStmt(BuildCommaExpr(s.Arg2)), BuildForCond(s.Arg4), BuildForPost(s.Arg6), BuildStmt(s.Arg8)) { Pos = pos };
            case C.StmtForNoInit s:
                return new For(null, BuildForCond(s.Arg3), BuildForPost(s.Arg5), BuildStmt(s.Arg7)) { Pos = pos };
            case C.StmtSwitch s: return BuildSwitch(s, pos);
            case C.StmtGoto s: return new Goto(DotCC.EmitHelpers.Id(Tok(s.Arg1))) { Pos = pos };
            case C.StmtLabel s: return new Labeled(DotCC.EmitHelpers.Id(Tok(s.Arg0)), BuildStmt(s.Arg2)) { Pos = pos };
            // A block-scope enum definition has no storage — register its
            // constants and emit nothing (an empty block).
            case C.StmtEnumDef s: RegisterEnum(Tok(s.Arg1), null, s.Arg3); return new Block(System.Array.Empty<CStmt>()) { Pos = pos };
            case C.StmtEnumDefTyped s: RegisterEnum(Tok(s.Arg1), s.Arg3, s.Arg5); return new Block(System.Array.Empty<CStmt>()) { Pos = pos };
            default: throw new IrUnsupportedException(TypeName(it.Content));
        }
    }

    /// <summary>Build a <see cref="Switch"/>. The grammar parses <c>case E:</c> /
    /// <c>default:</c> as statement-level labels whose body is the following
    /// statement (stacked labels nest: <c>case 0: case 1: stmt</c> is
    /// <c>CaseLabel(0, CaseLabel(1, stmt))</c>), so the body is a flat statement
    /// list with labels sprinkled in. Walk it, opening a new section whenever a
    /// label follows body statements and stacking consecutive labels into one
    /// section.</summary>
    private CStmt BuildSwitch(C.StmtSwitch s, SrcPos pos)
    {
        var subject = BuildExpr(s.Arg2);
        var raw = new List<Item>();
        if (s.Arg4.Content is C.Block b) { FlattenStmts(b.Arg2, raw.Add); }

        var sections = new List<SwitchSection>();
        var labels = new List<SwitchLabel>();
        var body = new List<CStmt>();
        var open = false;   // a section has been started (≥1 label seen)

        void Flush()
        {
            if (open) { sections.Add(new SwitchSection(labels, body)); }
            labels = new List<SwitchLabel>();
            body = new List<CStmt>();
            open = false;
        }

        void Walk(Item it)
        {
            switch (it.Content)
            {
                case C.CaseLabel cl:
                    if (body.Count > 0) { Flush(); }   // label after body → new section
                    open = true;
                    labels.Add(new SwitchLabel(BuildExpr(cl.Arg1)));
                    Walk(cl.Arg3);
                    break;
                case C.DefaultLabel dl:
                    if (body.Count > 0) { Flush(); }
                    open = true;
                    labels.Add(new SwitchLabel(null));
                    Walk(dl.Arg2);
                    break;
                default:
                    // A statement before the first case label is unreachable in C;
                    // drop it (C# would reject it anyway).
                    if (open) { body.Add(BuildStmt(it)); }
                    break;
            }
        }

        _symbols.EnterScope();
        foreach (var it in raw) { Walk(it); }
        Flush();
        _symbols.ExitScope();
        return new Switch(subject, sections) { Pos = pos };
    }

    /// <summary>Recognise the <c>setjmp</c>/<c>longjmp</c> guard idiom in an
    /// <c>if</c> and desugar it to a <see cref="SetjmpGuard"/>. Returns null when
    /// the condition is not a setjmp guard (the caller then builds a plain <see
    /// cref="If"/>). Recognition runs on the already-built IR — never on emitted
    /// text:
    /// <list type="bullet">
    ///   <item><c>if (setjmp(env)) recovery [else normal]</c> — setjmp is truthy
    ///     ONLY on the longjmp re-entry, so the then-branch is the recovery.</item>
    ///   <item><c>if (setjmp(env) == 0) normal [else recovery]</c> — the compare
    ///     is true on the direct (zero) return, so the then-branch is normal.</item>
    ///   <item><c>if (setjmp(env) != 0) recovery [else normal]</c> — the inverse.</item>
    /// </list></summary>
    private static CStmt? SetjmpGuardOf(CExpr cond, CStmt then, CStmt? els, SrcPos pos)
    {
        // Bare `if (setjmp(env)) …` — truthy only on the longjmp re-entry, so the
        // then-branch is the recovery (catch) and the absent/else side is the
        // normal (try) path.
        if (IsSetjmpCall(cond, out var bareEnv))
        {
            return new SetjmpGuard(bareEnv, TryBody: els, CatchBody: then) { Pos = pos };
        }
        // `setjmp(env) == 0` / `!= 0`, either operand order.
        if (cond is Binary { Op: BinOp.Eq or BinOp.Ne } b)
        {
            CExpr? env = null;
            if (IsSetjmpCall(b.Left, out var e1) && IsZeroLit(b.Right)) { env = e1; }
            else if (IsSetjmpCall(b.Right, out var e2) && IsZeroLit(b.Left)) { env = e2; }
            if (env is not null)
            {
                // `== 0` is true on the direct (zero) return; `!= 0` is true on the
                // re-entry. The "true on direct return" branch is the try (normal)
                // body; the other is the catch (recovery).
                var trueOnDirect = b.Op == BinOp.Eq;
                var tryBody = trueOnDirect ? then : els;
                var catchBody = trueOnDirect ? els : then;
                return new SetjmpGuard(env, tryBody, catchBody) { Pos = pos };
            }
        }
        return null;
    }

    /// <summary>True when <paramref name="e"/> is a direct call to <c>setjmp</c>
    /// (parens peeled); yields the env-token argument expression.</summary>
    private static bool IsSetjmpCall(CExpr e, out CExpr env)
    {
        while (e is Paren p) { e = p.Inner; }
        if (e is Call { Callee: "setjmp", Args: { Count: 1 } a })
        {
            env = a[0];
            return true;
        }
        env = null!;
        return false;
    }

    /// <summary>True for the integer literal <c>0</c> (parens peeled) — the RHS of
    /// a recognised <c>setjmp(env) == 0</c> guard.</summary>
    private static bool IsZeroLit(CExpr e)
    {
        while (e is Paren p) { e = p.Inner; }
        return e is LitInt { Value: 0 };
    }

    /// <summary>A declaration in statement position. The grammar wraps the
    /// concrete declaration kind (plain, array, fn-ptr, …) inside
    /// <c>StmtDecl.Arg0</c>; dispatch on it.</summary>
    private CStmt BuildDeclStmt(Item it) => it.Content switch
    {
        C.Decl => BuildDecl(it),
        // `auto x = E;` (C23 type inference) and `auto Type x …` (redundant
        // pre-C23 storage class). See BuildDeclAutoInfer / BuildDeclList.
        C.DeclAutoInfer d => BuildDeclAutoInfer(d),
        C.DeclAutoStorage d => BuildDeclList(d.Arg1, d.Arg2),
        // `char s[] = "hi";` (implicit size) / `char buf[N] = "hi";` (explicit, zero-padded).
        C.DeclCharArrStr d => BuildDeclCharArrStr(d.Arg0, d.Arg1, null, d.Arg5),
        C.DeclCharArrStrSized d => BuildDeclCharArrStr(d.Arg0, d.Arg1, CharArrSize(d.Arg2), d.Arg4),
        C.DeclStructInit d => BuildDeclStructInit(d),
        // `T x = { .field = … };` — C99 designated struct/union initializer.
        C.DeclStructDesignated d => BuildLocalInit(d.Arg1, ResolveType(d.Arg0), BuildStructDesignated(ResolveType(d.Arg0), d.Arg4)),
        // `T x = {};` — C23 empty initializer (zero value).
        C.DeclEmptyInit d => Gated(2023, "empty initializer", d.Arg1, BuildLocalInit(d.Arg1, ResolveType(d.Arg0), new DefaultLit { Type = ResolveType(d.Arg0) })),
        C.DeclArr d => BuildArrDecl(d.Arg0, d.Arg1, d.Arg2, null, implicitSize: false),
        C.DeclArrEmptyInit d => BuildArrDecl(d.Arg0, d.Arg1, d.Arg2, null, implicitSize: false),
        C.DeclArrInit d => BuildArrDecl(d.Arg0, d.Arg1, d.Arg2, d.Arg5, implicitSize: false),
        C.DeclArrInitImplicit d => BuildArrDecl(d.Arg0, d.Arg1, null, d.Arg6, implicitSize: true),
        // Pointer-to-array `T (*p)[N]` [= init] — a row pointer (multi-dim machinery).
        C.DeclPtrToArr d => BuildPtrToArr(d.Arg0, d.Arg3, d.Arg5, null),
        C.DeclPtrToArrInit d => BuildPtrToArr(d.Arg0, d.Arg3, d.Arg5, d.Arg7),
        // Local function-pointer variable: `Ret (*name)(params)` [= init].
        C.DeclFnPtr d => BuildFnPtrLocal(d.Arg0, d.Arg3, d.Arg6, null),
        C.DeclFnPtrNoArgs d => BuildFnPtrLocal(d.Arg0, d.Arg3, null, null),
        C.DeclFnPtrInit d => BuildFnPtrLocal(d.Arg0, d.Arg3, d.Arg6, d.Arg9),
        C.DeclFnPtrNoArgsInit d => BuildFnPtrLocal(d.Arg0, d.Arg3, null, d.Arg8),
        _ => throw new IrUnsupportedException(TypeName(it.Content)),
    };

    /// <summary>Per-translation-unit counter giving each function-scope
    /// <c>static</c> local a program-unique backing-field name. The CS0136
    /// per-function rename can't serve here — two functions' same-named statics
    /// become two fields of the SAME <c>DotCcGlobals</c> class.</summary>
    private int _staticLocalSeq;

    /// <summary>A function-scope <c>static</c> local (<c>static int counter = 0;</c>).
    /// Its storage is program-lifetime, so it lowers to a once-initialized static
    /// field of <c>DotCcGlobals</c> (a <see cref="GlobalVar"/> with a mangled,
    /// program-unique name); references resolve to that field via an alias symbol
    /// in the function's scope. The statement itself emits nothing.</summary>
    private CStmt BuildStmtStaticDecl(C.StmtStaticDecl n)
    {
        WalkDeclList(n.Arg1, n.Arg2, (name, initItem, type) =>
        {
            var sym = new Symbol
            {
                Name = name, Kind = SymKind.Var, Type = type,
                Storage = Storage.Static, IsGlobal = true,
                CsName = $"{DotCC.EmitHelpers.Id(name)}__s{_staticLocalSeq++}",
            };
            Globals.Add(new GlobalVar(sym, initItem is { } ii ? BuildExpr(ii) : null));
            _symbols.DeclareAlias(sym);
        });
        return new DeclStmt(System.Array.Empty<LocalDecl>());
    }

    /// <summary>File-scope <c>static T x = { … };</c> — a once-initialised
    /// <c>DotCcGlobals</c> field with a positional aggregate initializer.</summary>
    private void BuildGlobalStructInit(Item typeItem, Item nameItem, Item initListItem)
    {
        var type = ResolveType(typeItem);
        var init = BuildAggregateInit(type, initListItem);
        var sym = _symbols.Declare(new Symbol
        {
            Name = Tok(nameItem), Kind = SymKind.Var, Type = type, Storage = Storage.Static, IsGlobal = true,
        });
        Globals.Add(new GlobalVar(sym, init));
    }

    /// <summary>Block-scope <c>static T x = { … };</c> — like a global aggregate
    /// init, but with a program-unique mangled name and an alias symbol so the
    /// function body's references resolve to the hoisted field.</summary>
    private CStmt BuildStmtStaticStructInit(C.StmtStaticStructInit n)
    {
        var type = ResolveType(n.Arg1);
        var init = BuildAggregateInit(type, n.Arg5);
        var sym = new Symbol
        {
            Name = Tok(n.Arg2), Kind = SymKind.Var, Type = type,
            Storage = Storage.Static, IsGlobal = true,
            CsName = $"{DotCC.EmitHelpers.Id(Tok(n.Arg2))}__s{_staticLocalSeq++}",
        };
        Globals.Add(new GlobalVar(sym, init));
        _symbols.DeclareAlias(sym);
        return new DeclStmt(System.Array.Empty<LocalDecl>());
    }

    private DeclStmt BuildFnPtrLocal(Item retItem, Item nameItem, Item? paramsItem, Item? initItem)
    {
        var type = FnPtrType(retItem, paramsItem);
        var sym = _symbols.Declare(new Symbol { Name = Tok(nameItem), Kind = SymKind.Var, Type = type, Storage = Storage.Auto });
        return new DeclStmt(new[] { new LocalDecl(sym, initItem is { } ii ? BuildExpr(ii) : null) });
    }

    private DeclStmt BuildDecl(Item declItem)
    {
        if (declItem.Content is not C.Decl decl) { throw new IrUnsupportedException(TypeName(declItem.Content)); }
        return BuildDeclList(decl.Arg0, decl.Arg1);
    }

    /// <summary>Build a <c>Type DeclItemList</c> declaration into a
    /// <see cref="DeclStmt"/>. Shared by the plain form (<c>int x, y;</c>) and the
    /// redundant pre-C23 storage-class form (<c>auto int z;</c>), which differ
    /// only in the dropped <c>auto</c> keyword.</summary>
    private DeclStmt BuildDeclList(Item typeItem, Item listItem)
    {
        var entries = new List<LocalDecl>();
        WalkDeclList(typeItem, listItem, (name, initItem, type) =>
        {
            var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = type, Storage = Storage.Auto });
            entries.Add(new LocalDecl(sym, initItem is { } ii ? BuildExpr(ii) : null));
        });
        return new DeclStmt(entries);
    }

    /// <summary>C23 type inference — <c>auto x = E;</c> deduces <c>x</c>'s type
    /// from its initializer. The IR already synthesizes a <see cref="CType"/> on
    /// every expression, so the declared symbol simply takes the initializer's
    /// type and lowers like any scalar local (codegen prints the inferred C#
    /// type). gcc <c>__auto_type</c> / C++ <c>auto</c> / C# <c>var</c> shape.</summary>
    private DeclStmt BuildDeclAutoInfer(C.DeclAutoInfer n)
    {
        Gate(2023, "auto` type inference", n.Arg1);
        var init = BuildExpr(n.Arg3);
        var sym = _symbols.Declare(new Symbol { Name = Tok(n.Arg1), Kind = SymKind.Var, Type = init.Type, Storage = Storage.Auto });
        return new DeclStmt(new[] { new LocalDecl(sym, init) });
    }

    /// <summary>Walk a comma-separated init-declarator list, invoking
    /// <paramref name="add"/> with each declarator's (name, initializer?, type).
    /// The FIRST declarator's <c>*</c>s were greedily folded into
    /// <paramref name="baseType"/> by the grammar's <c>Type → Type *</c> rule;
    /// each subsequent declarator rebuilds its type from the pointer-stripped
    /// element plus its own <c>*</c>s (so <c>int *a, b;</c> ⇒ a:int*, b:int).
    /// Shared by local (<see cref="BuildDecl"/>) and file-scope declarations.</summary>
    private void WalkDeclList(Item typeItem, Item listItem, Action<string, Item?, CType> add)
    {
        var baseType = ResolveType(typeItem);
        // Peel only the LITERAL trailing `*`s (the `Type → Type *` rule greedily
        // folded them into baseType, but they bind to the FIRST declarator alone) —
        // subsequent declarators rebuild from the stripped element plus their own
        // `*`s (`int *a, b` ⇒ a:int*, b:int). Pointer-ness that came from a typedef
        // base (`BoxPtr p, q` ⇒ both Box*) is NOT a literal star and stays in
        // `element`, so every declarator keeps it.
        var litStars = CountLiteralStars(typeItem);
        var element = baseType;
        for (var i = 0; i < litStars && element is CType.Pointer p; i++) { element = p.Pointee; }
        void WalkTail(Item it, int stars)
        {
            switch (it.Content)
            {
                // `DeclItemTail → * DeclItemTail` (declItemTailPtr) — its child is
                // itself a DeclItemTail, so it can be a further `*` level OR the
                // terminal `DeclItemTailPlain` wrapping the DeclItem. Both recurse,
                // accumulating the star count (`int *a, *b;` → b:int*).
                case C.DeclItemTailPtr p: WalkTail(p.Arg1, stars + 1); break;
                case C.DeclItemTailPlain t: WalkTail(t.Arg0, stars); break;
                case C.DeclItem di: add(Tok(di.Arg0), null, WrapPtr(element, stars)); break;
                case C.DeclItemInit di: add(Tok(di.Arg0), di.Arg2, WrapPtr(element, stars)); break;
                default: throw new IrUnsupportedException(TypeName(it.Content));
            }
        }
        void Walk(Item it)
        {
            switch (it.Content)
            {
                case C.DeclItemListCons c: Walk(c.Arg0); Walk(c.Arg2); break;
                case C.DeclItemListOne o: Walk(o.Arg0); break;
                case C.DeclItem di: add(Tok(di.Arg0), null, baseType); break;
                case C.DeclItemInit di: add(Tok(di.Arg0), di.Arg2, baseType); break;
                case C.DeclItemTailPlain t: WalkTail(t.Arg0, 0); break;
                case C.DeclItemTailPtr t: WalkTail(t.Arg1, 1); break;
                default: throw new IrUnsupportedException(TypeName(it.Content));
            }
        }
        Walk(listItem);
    }

    private static CType WrapPtr(CType t, int stars)
    {
        for (var i = 0; i < stars; i++) { t = new CType.Pointer(t); }
        return t;
    }

    /// <summary>Count the literal trailing <c>*</c>s on a type node (the
    /// <c>Type → Type *</c> pointer-qualifier chain). Distinguishes a pointer
    /// written with <c>*</c> in the source (binds to the first declarator only)
    /// from one a typedef base contributes (binds to every declarator).</summary>
    private static int CountLiteralStars(Item typeItem)
    {
        var n = 0;
        var it = typeItem;
        while (true)
        {
            switch (it.Content)
            {
                case C.TypePtr t: n++; it = t.Arg0; break;
                case C.TypePtrQualConst t: n++; it = t.Arg0; break;
                case C.TypePtrQualVolatile t: n++; it = t.Arg0; break;
                case C.TypePtrQualRestrict t: n++; it = t.Arg0; break;
                default: return n;
            }
        }
    }

    /// <summary>A local array declaration. Lowers to a C# <c>stackalloc</c>. A
    /// constant dimension types the symbol as <see cref="CType.Array"/> (so
    /// <c>sizeof(arr)</c> and the array-length idiom resolve); a runtime extent
    /// (VLA-ish) decays to a pointer. Multi-dimensional arrays are deferred.</summary>
    private ArrayDecl BuildArrDecl(Item typeItem, Item nameItem, Item? dimsItem, Item? initItem, bool implicitSize)
    {
        var elem = ResolveType(typeItem);
        var name = Tok(nameItem);
        var dims = dimsItem is { } di ? TryConstDims(di) : null;

        CType arrType;
        CExpr? countExpr = null;
        List<CExpr>? inits = null;
        if (initItem is { } ii)
        {
            // Brace-initialized — the array interpreter resolves designators,
            // struct elements, and per-dimension zero-fill into a dense list; a
            // multi-dim array flattens to one stackalloc of product(dims). The
            // symbol keeps the NESTED array type so a[i][j] strides correctly.
            inits = BuildArrayElems(elem, dims, ParseInitList(ii));
            arrType = dims is { Count: >= 1 } ? MakeArrayType(elem, dims) : new CType.Array(elem, inits.Count);
        }
        else if (dims is { Count: >= 1 })
        {
            // Fixed-size, no initializer (C# zero-fills a stackalloc). A multi-dim
            // array flattens to one stackalloc of the product; sizeof(arr) and the
            // array-length idiom read the dimensions off the nested array type.
            var total = 1;
            foreach (var d in dims) { total *= d; }
            arrType = MakeArrayType(elem, dims);
            countExpr = new LitInt(total.ToString(System.Globalization.CultureInfo.InvariantCulture), total) { Type = CType.Int };
        }
        else if (dimsItem is { } di2 && BuildArrDims(di2) is { Count: 1 } runtimeDims)
        {
            // Runtime extent (VLA) — C arrays decay to a pointer in value context.
            arrType = new CType.Pointer(elem);
            countExpr = runtimeDims[0];
        }
        else if (dimsItem is { })
        {
            throw new IrUnsupportedException("multi-dimensional array with a non-constant dimension");
        }
        else
        {
            arrType = new CType.Pointer(elem);
        }

        var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = arrType, Storage = Storage.Auto });
        return new ArrayDecl(sym, elem, countExpr, inits);
    }

    /// <summary>Collect the dimension expressions of an <c>ArrDims</c> node,
    /// outer→inner.</summary>
    private List<CExpr> BuildArrDims(Item it)
    {
        var dims = new List<CExpr>();
        void Walk(Item n)
        {
            switch (n.Content)
            {
                case C.ArrDimsCons c: Walk(c.Arg0); dims.Add(BuildExpr(c.Arg2)); break;
                case C.ArrDimsOne o: dims.Add(BuildExpr(o.Arg1)); break;
                default: throw new IrUnsupportedException(TypeName(n.Content));
            }
        }
        Walk(it);
        return dims;
    }

    /// <summary><c>struct Point p = {3, 4};</c> — a local declaration with a
    /// positional aggregate initializer. Types the symbol as the struct and
    /// builds a <see cref="StructInit"/> from the brace list.</summary>
    private CStmt BuildDeclStructInit(C.DeclStructInit n)
    {
        var type = ResolveType(n.Arg0);
        return BuildLocalInit(n.Arg1, type, BuildAggregateInit(type, n.Arg4));
    }

    /// <summary>Declare a single block local of <paramref name="type"/> with an
    /// already-built initializer, as a one-declarator <see cref="DeclStmt"/>.</summary>
    private DeclStmt BuildLocalInit(Item nameItem, CType type, CExpr init)
    {
        var sym = _symbols.Declare(new Symbol { Name = Tok(nameItem), Kind = SymKind.Var, Type = type, Storage = Storage.Auto });
        return new DeclStmt(new[] { new LocalDecl(sym, init) });
    }

    /// <summary>A positional struct/union aggregate initializer from an
    /// <c>InitList</c> — shared by local, file-scope, and static-local struct
    /// inits (see <see cref="BuildStructPositional"/>).</summary>
    private StructInit BuildAggregateInit(CType type, Item initListItem) =>
        BuildStructPositional(type, ParseInitList(initListItem));

    private For BuildForDecl(C.StmtForDecl s)
    {
        // for ( <decl> ; <cond> ; <post> ) <body> — Arg2 is the ScopeEnter epsilon;
        // Decl=Arg3, ForCond=Arg5, ForPost=Arg7, body=Arg9 (see legacy StmtForDecl).
        _symbols.EnterScope();
        Gate(1999, "for-loop initializer declaration", s.Arg3);
        var init = BuildDecl(s.Arg3);
        var cond = BuildForCond(s.Arg5);
        var post = BuildForPost(s.Arg7);
        var body = BuildStmt(s.Arg9);
        _symbols.ExitScope();
        return new For(init, cond, post, body);
    }

    private CExpr? BuildForCond(Item it) => it.Content switch
    {
        C.ForCondExpr e => BuildExpr(e.Arg0),
        C.ForCondEmpty => null,
        _ => throw new IrUnsupportedException(TypeName(it.Content)),
    };

    private CExpr? BuildForPost(Item it) => it.Content switch
    {
        C.ForPostEmpty => null,
        C.ForPostExprs e => BuildCommaExpr(e.Arg0),
        _ => throw new IrUnsupportedException(TypeName(it.Content)),
    };

    /// <summary>A comma-separated expression list (for-init / for-update). One
    /// expression passes through; several become a <see cref="CommaSeq"/> that
    /// codegen renders as C#'s native <c>a, b</c> list in those positions.</summary>
    private CExpr BuildCommaExpr(Item it)
    {
        var items = new List<CExpr>();
        void Walk(Item node)
        {
            switch (node.Content)
            {
                case C.CommaExprCons c: Walk(c.Arg0); items.Add(BuildExpr(c.Arg2)); break;
                case C.CommaExprOne o: items.Add(BuildExpr(o.Arg0)); break;
                default: items.Add(BuildExpr(node)); break;
            }
        }
        Walk(it);
        return items.Count == 1 ? items[0] : new CommaSeq(items) { Type = items[^1].Type };
    }

    // ---- expressions -----------------------------------------------------

    private CExpr BuildExpr(Item it)
    {
        var pos = SrcPos.From(it);
        CExpr e = it.Content switch
        {
            C.Num n => BuildNum(n),
            C.Flt f => BuildFloat(f),
            C.Str => BuildStr(it),
            C.Chr c => BuildChr(c),
            C.Var v => BuildVar(v),
            C.Paren p => BuildExpr(p.Arg1) is var inner ? new Paren(inner) { Type = inner.Type, IsLValue = inner.IsLValue } : throw new InvalidOperationException(),
            C.Add b => Bin(BinOp.Add, b.Arg0, b.Arg2),
            C.Sub b => Bin(BinOp.Sub, b.Arg0, b.Arg2),
            C.Mul b => Bin(BinOp.Mul, b.Arg0, b.Arg2),
            C.Div b => Bin(BinOp.Div, b.Arg0, b.Arg2),
            C.Mod b => Bin(BinOp.Mod, b.Arg0, b.Arg2),
            C.Shl b => Bin(BinOp.Shl, b.Arg0, b.Arg2),
            C.Shr b => Bin(BinOp.Shr, b.Arg0, b.Arg2),
            C.BAnd b => Bin(BinOp.BitAnd, b.Arg0, b.Arg2),
            C.BOr b => Bin(BinOp.BitOr, b.Arg0, b.Arg2),
            C.BXor b => Bin(BinOp.BitXor, b.Arg0, b.Arg2),
            C.Lt b => Rel(BinOp.Lt, b.Arg0, b.Arg2),
            C.Gt b => Rel(BinOp.Gt, b.Arg0, b.Arg2),
            C.Le b => Rel(BinOp.Le, b.Arg0, b.Arg2),
            C.Ge b => Rel(BinOp.Ge, b.Arg0, b.Arg2),
            C.Eq b => Rel(BinOp.Eq, b.Arg0, b.Arg2),
            C.Neq b => Rel(BinOp.Ne, b.Arg0, b.Arg2),
            C.Land b => Rel(BinOp.LogAnd, b.Arg0, b.Arg2),
            C.Lor b => Rel(BinOp.LogOr, b.Arg0, b.Arg2),
            C.Assign a => Asn(null, a.Arg0, a.Arg2),
            C.AddAssign a => Asn(BinOp.Add, a.Arg0, a.Arg2),
            C.SubAssign a => Asn(BinOp.Sub, a.Arg0, a.Arg2),
            C.MulAssign a => Asn(BinOp.Mul, a.Arg0, a.Arg2),
            C.DivAssign a => Asn(BinOp.Div, a.Arg0, a.Arg2),
            C.ModAssign a => Asn(BinOp.Mod, a.Arg0, a.Arg2),
            C.AndAssign a => Asn(BinOp.BitAnd, a.Arg0, a.Arg2),
            C.OrAssign a => Asn(BinOp.BitOr, a.Arg0, a.Arg2),
            C.XorAssign a => Asn(BinOp.BitXor, a.Arg0, a.Arg2),
            C.ShlAssign a => Asn(BinOp.Shl, a.Arg0, a.Arg2),
            C.ShrAssign a => Asn(BinOp.Shr, a.Arg0, a.Arg2),
            C.PreInc u => Un(UnOp.PreInc, u.Arg1),
            C.PreDec u => Un(UnOp.PreDec, u.Arg1),
            C.PostInc u => Un(UnOp.PostInc, u.Arg0),
            C.PostDec u => Un(UnOp.PostDec, u.Arg0),
            C.Neg u => Un(UnOp.Neg, u.Arg1),
            C.BNot u => Un(UnOp.BitNot, u.Arg1),
            C.LNot u => Un(UnOp.LogNot, u.Arg1),
            C.Deref u => Un(UnOp.Deref, u.Arg1),
            C.AddrOf u => Un(UnOp.AddrOf, u.Arg1),
            C.Ternary t => BuildTernary(t),
            C.Subscript s => BuildIndex(s),
            C.MemberDot m => BuildMember(m.Arg0, m.Arg2, arrow: false),
            C.MemberArrow m => BuildMember(m.Arg0, m.Arg2, arrow: true),
            C.Cast c => BuildCast(c),
            C.LitTrue => new LitInt("1", 1) { Type = CType.Int },
            C.LitFalse => new LitInt("0", 0) { Type = CType.Int },
            C.LitNullptr => new Raw("null") { Type = new CType.Pointer(CType.Void) },
            C.SizeofType s => new SizeOfExpr(ResolveType(s.Arg2)) { Type = CType.SizeT },
            // `sizeof expr` — the operand isn't evaluated, only its type measured.
            C.SizeofExpr s => new SizeOfExpr(BuildExpr(s.Arg1).Type) { Type = CType.SizeT },
            C.OffsetofExpr o => BuildOffsetof(o),
            // `va_arg(ap, T)` — special syntax (its 2nd operand is a type).
            C.VaArgExpr v => new VaArgGet(BuildExpr(v.Arg2), ResolveType(v.Arg4)) { Type = ResolveType(v.Arg4) },
            C.Call c => BuildCall(c.Arg0, c.Arg2),
            C.CallNoArgs c => BuildCall(c.Arg0, null),
            // C99/C23 compound literals — (T){…} struct/scalar, (T[]){…} array,
            // designated, and the C23 empty form.
            C.CompoundLit c => Gated(1999, "compound literals", c.Arg1, BuildCompoundLit(c.Arg1, c.Arg4)),
            C.CompoundLitDesignated c => Gated(1999, "compound literals", c.Arg1, BuildCompoundLitDesignated(c.Arg1, c.Arg4)),
            C.CompoundLitEmpty c => Gated(2023, "empty initializer", c.Arg1, BuildCompoundLitEmpty(c.Arg1)),
            C.CompoundLitArr c => Gated(1999, "compound literals", c.Arg1, BuildArrayCompoundLit(c.Arg1, c.Arg2, c.Arg5)),
            C.CompoundLitArrImplicit c => Gated(1999, "compound literals", c.Arg1, BuildArrayCompoundLit(c.Arg1, null, c.Arg6)),
            C.CommaOp => BuildCommaOp(it),
            _ => throw new IrUnsupportedException(TypeName(it.Content)),
        };
        return e with { Pos = pos };
    }

    private CExpr BuildTernary(C.Ternary t)
    {
        var cond = BuildExpr(t.Arg0);
        var then = BuildExpr(t.Arg2);
        var els = BuildExpr(t.Arg4);
        // Result type: arithmetic arms reconcile per the usual conversions; if either
        // arm is a pointer/array/function the result is THAT (C: `cond ? ptr : NULL`
        // is the pointer type, not the null constant's int) — else the then-arm's type.
        static bool IsPtrish(CType t) => t.Unqualified is CType.Pointer or CType.Array or CType.Func;
        var ty = then.Type.IsArithmetic && els.Type.IsArithmetic ? CType.UsualArithmetic(then.Type, els.Type)
               : IsPtrish(then.Type) ? then.Type
               : IsPtrish(els.Type) ? els.Type
               : then.Type;
        return new CondExpr(cond, then, els) { Type = ty };
    }

    private CExpr BuildIndex(C.Subscript s)
    {
        var base_ = BuildExpr(s.Arg0);
        var idx = BuildExpr(s.Arg2);
        var elem = base_.Type switch
        {
            CType.Pointer p => p.Pointee,
            CType.Array a => a.Element,
            _ => CType.Int,
        };
        return new Index(base_, idx) { Type = elem, IsLValue = true };
    }

    private CExpr BuildMember(Item baseItem, Item fieldItem, bool arrow) =>
        BuildMemberAccess(BuildExpr(baseItem), Tok(fieldItem), arrow);

    /// <summary>Build a member access, routing a promoted (anonymous-aggregate)
    /// field through its hidden container: <c>v.i</c> on an aggregate whose <c>i</c>
    /// came from an anonymous <c>struct/union {…};</c> becomes <c>v.hidden.i</c>.
    /// Recurses so a field promoted through several nesting levels still resolves.</summary>
    private CExpr BuildMemberAccess(CExpr base_, string field, bool arrow)
    {
        if (StructCanonical(base_.Type) is { } canonical
            && _promoted.TryGetValue(canonical, out var pm)
            && pm.TryGetValue(field, out var p))
        {
            var hidden = new Member(base_, p.Hidden, arrow) { Type = new CType.Named(p.Nested), IsLValue = true };
            return BuildMemberAccess(hidden, field, arrow: false);
        }
        return new Member(base_, field, arrow) { Type = MemberType(base_, field), IsLValue = true };
    }

    /// <summary>The canonical struct/union name an expression's type names (pointer
    /// levels peeled), or null if it isn't an aggregate.</summary>
    private static string? StructCanonical(CType t)
    {
        while (t is CType.Pointer p) { t = p.Pointee; }
        return (t.Unqualified as CType.Named)?.Name;
    }

    private CExpr BuildCast(C.Cast c)
    {
        var target = ResolveType(c.Arg1);
        var operand = BuildExpr(c.Arg3);
        // `(void)X` — C# has no void cast and the value is discarded; carry the
        // operand through typed void so a statement position emits `X;`.
        if (target is CType.VoidType) { return operand with { Type = CType.Void }; }
        return new Cast(target, operand) { Type = target };
    }

    /// <summary>The value-context comma operator (<c>Expr → Expr ',' E</c>,
    /// left-associative). Flatten nested commas into a single ordered operand
    /// list — the leading operands are evaluated for side effects, the last is
    /// the value (and type).</summary>
    private CExpr BuildCommaOp(Item it)
    {
        var items = new List<CExpr>();
        void Walk(Item node)
        {
            if (node.Content is C.CommaOp c) { Walk(c.Arg0); items.Add(BuildExpr(c.Arg2)); }
            else { items.Add(BuildExpr(node)); }
        }
        Walk(it);
        return new CommaOp(items) { Type = items[^1].Type, IsLValue = items[^1].IsLValue };
    }

    private CExpr Bin(BinOp op, Item l, Item r)
    {
        var le = BuildExpr(l);
        var re = BuildExpr(r);
        return new Binary(op, le, re) { Type = BinaryType(op, le.Type, re.Type) };
    }

    private CExpr Rel(BinOp op, Item l, Item r) =>
        new Binary(op, BuildExpr(l), BuildExpr(r)) { Type = CType.Int };

    /// <summary>The C type of a binary arithmetic/bitwise/shift expression's
    /// result. Pointer arithmetic (<c>p + i</c> / <c>p - i</c>) yields the
    /// (decayed) pointer type and <c>p - q</c> yields <c>ptrdiff_t</c> (a signed
    /// 64-bit, <c>long</c> here); a shift yields its promoted left operand's type
    /// (the right operand doesn't take part); everything else takes the usual
    /// arithmetic conversions (<see cref="CType.UsualArithmetic"/>).</summary>
    private static CType BinaryType(BinOp op, CType l, CType r)
    {
        // C99 _Complex (lowered to System.Numerics.Complex): arithmetic with a
        // complex operand yields complex — its C# operators handle complex×real.
        if (IsComplexType(l) || IsComplexType(r)) { return CType.Complex; }
        static CType Decay(CType t) => t.Unqualified switch
        {
            CType.Array a => new CType.Pointer(a.Element),
            var u => u,
        };
        var lPtr = l.Unqualified is CType.Pointer or CType.Array;
        var rPtr = r.Unqualified is CType.Pointer or CType.Array;
        if (op is BinOp.Add or BinOp.Sub && (lPtr || rPtr))
        {
            return lPtr && rPtr ? CType.Long : Decay(lPtr ? l : r);
        }
        if (op is BinOp.Shl or BinOp.Shr)
        {
            return l.Unqualified is CType.Prim { Integer: true, Bytes: < 4 } ? CType.Int : l.Unqualified;
        }
        return CType.UsualArithmetic(l, r);
    }

    private CExpr Asn(BinOp? op, Item l, Item r)
    {
        var le = BuildExpr(l);
        return new Assign(op, le, BuildExpr(r)) { Type = le.Type, IsLValue = false };
    }

    private CExpr Un(UnOp op, Item operand)
    {
        var oe = BuildExpr(operand);
        // Taking the address of a pointer/fn-ptr-typed GLOBAL can't go through
        // Unsafe.AsPointer<T> (T may not be a pointer — CS0306). Mark the symbol so
        // codegen declares its backing field as `nint` and casts on every use.
        if (op == UnOp.AddrOf && Unparen(oe) is VarRef { Sym: { IsGlobal: true, Kind: SymKind.Var } gsym }
            && gsym.Type.IsPointerLowered)
        {
            gsym.StoreAsNint = true;
        }
        CType t = op switch
        {
            UnOp.LogNot => CType.Int,
            UnOp.AddrOf => new CType.Pointer(oe.Type),
            // *p → pointee; *arr (incl. a string literal, typed char[]) → its element
            // (the array decays to a pointer first). *ptr-to-array stays the array,
            // which codegen treats as a no-op decay back to the row pointer.
            UnOp.Deref => oe.Type.Unqualified switch
            {
                CType.Pointer p => p.Pointee,
                CType.Array a => a.Element,
                _ => oe.Type,
            },
            _ => oe.Type,
        };
        return new Unary(op, oe) { Type = t, IsLValue = op == UnOp.Deref };
    }

    /// <summary>Peel redundant <see cref="Paren"/> wrappers to reach the inner expr.</summary>
    private static CExpr Unparen(CExpr e) => e is Paren p ? Unparen(p.Inner) : e;

    private CExpr BuildVar(C.Var v)
    {
        var name = Tok(v.Arg0);
        var sym = _symbols.Resolve(name);
        if (sym is { Kind: SymKind.EnumConst })
        {
            // An enumerator of a real (named) enum renders as EnumName.Member; one of
            // an anonymous int-constant enum lowers to its literal integer value.
            return sym.Type.Unqualified is CType.Enum
                ? new EnumConstRef(sym) { Type = sym.Type }
                : new LitInt(sym.ConstValue.ToString(System.Globalization.CultureInfo.InvariantCulture), sym.ConstValue) { Type = CType.Int };
        }
        if (sym is not null)
        {
            return new VarRef(sym) { Type = sym.Type, IsLValue = sym.Kind is SymKind.Var or SymKind.Param };
        }
        // `__func__` (C99 §6.4.2.2) — a predefined identifier implicitly declared
        // at the start of every function body as `static const char __func__[] =
        // "<name>";`. It is not a macro (the preprocessor never sees it), so it
        // surfaces here as an unbound name; resolve it to a string literal of the
        // enclosing function's name. (`__FILE__`/`__LINE__` ARE macros and are
        // already expanded upstream by the preprocessor.)
        // The imaginary unit — <complex.h> expands `I` → `_Complex_I` →
        // `__dotcc_complex_I`, a Libc static of System.Numerics.Complex surfaced by
        // `using static Libc`. Type it complex so `a + b*I` propagates correctly.
        if (name is "__dotcc_complex_I")
        {
            return new Raw("__dotcc_complex_I") { Type = CType.Complex };
        }
        if (name is "__func__" && _currentFnName.Length != 0)
        {
            var lit = DotCC.EmitHelpers.EncodeStringLiteral(new[] { $"\"{_currentFnName}\"" }, out var fnLen);
            return new LitStr(lit) { Type = new CType.Array(CType.Char, fnLen) };
        }
        // Unresolved (a macro-substituted token, a builtin not in a header). Emit
        // the escaped raw name verbatim and let Roslyn arbitrate. Slice code never
        // hits this; it's a safety net during incremental growth.
        return new Raw(DotCC.EmitHelpers.Id(name)) { Type = CType.Int };
    }

    private CExpr BuildCall(Item calleeItem, Item? argList)
    {
        var args = new List<CExpr>();
        if (argList is { } al) { FlattenArgs(al, x => args.Add(BuildExpr(x))); }

        // A simple named callee — a function, a fn-ptr variable, or a libc builtin.
        if (TryCalleeName(calleeItem, out var name))
        {
            // The resolved signature's parameter types drive call-argument
            // coercion (C's implicit conversion at a call, e.g. `size_t` sizeof
            // arg → `int` malloc param, or the int-0 null-pointer constant).
            var fn = _symbols.Resolve(name)?.Type as CType.Func;
            return new Call(name, args, IsPrintfFamily(name), fn?.Params) { Type = fn?.Return ?? CType.Int };
        }

        // Indirect call through a computed fn-ptr expression: `(*fp)(x)`,
        // `tbl[i](x)`, `s.fn(x)`. Dereferencing a function pointer is a C no-op
        // that decays straight back to the pointer; C# calls fn-ptrs directly
        // and rejects `*fp` (CS0193). Peel redundant parens, then any leading
        // deref whose operand is itself a function pointer — repeatedly, so
        // `(*(e.op))(x)` and the parenthesised `(*f)(x)` forms both reduce. A
        // deref of a pointer-TO-fn-pointer is preserved: C# needs it to reach
        // the callable value.
        var callee = BuildExpr(calleeItem);
        while (true)
        {
            if (callee is Paren pc) { callee = pc.Inner; continue; }
            if (callee is Unary { Op: UnOp.Deref } u && IsFuncPtr(u.Operand.Type)) { callee = u.Operand; continue; }
            break;
        }
        var rty = callee.Type switch
        {
            CType.Func f2 => f2.Return,
            CType.Pointer { Pointee: CType.Func f3 } => f3.Return,
            _ => CType.Int,
        };
        return new IndirectCall(callee, args) { Type = rty };
    }

    /// <summary>True when <paramref name="t"/> is a function pointer (or a bare
    /// function type) — the operand of a no-op call-site deref. Typedefs already
    /// resolve to their underlying type at <c>ResolveType</c> time, so a plain
    /// structural check on the unqualified type suffices.</summary>
    private static bool IsFuncPtr(CType t) =>
        t.Unqualified is CType.Pointer { Pointee: CType.Func } or CType.Func;

    /// <summary>True when <paramref name="t"/> is the lowered C99 <c>_Complex</c>
    /// type (<c>System.Numerics.Complex</c>).</summary>
    private static bool IsComplexType(CType t) =>
        t.Unqualified is CType.Named { Name: "System.Numerics.Complex" };

    private bool TryCalleeName(Item it, out string name)
    {
        switch (it.Content)
        {
            case C.Var v: name = Tok(v.Arg0); return true;
            case C.Paren p: return TryCalleeName(p.Arg1, out name);
            default: name = ""; return false;
        }
    }

    private void FlattenArgs(Item it, Action<Item> onArg)
    {
        switch (it.Content)
        {
            case C.ArgsCons c: onArg(c.Arg0); FlattenArgs(c.Arg2, onArg); break;
            case C.ArgsOne o: onArg(o.Arg0); break;
            default: onArg(it); break;
        }
    }

    private static bool IsPrintfFamily(string name) =>
        name is "printf" or "fprintf" or "sprintf" or "snprintf";

    // ---- literals --------------------------------------------------------

    /// <summary><c>offsetof(T, member)</c> — resolve the aggregate type and look
    /// up the member's field type so codegen knows whether it is a primitive
    /// <c>fixed</c>-buffer (whose access already yields its address, so no
    /// <c>&amp;</c>). The actual offset is computed at runtime by the
    /// null-pointer idiom in codegen, matching the .NET blittable layout.</summary>
    private CExpr BuildOffsetof(C.OffsetofExpr n)
    {
        var structType = ResolveType(n.Arg2);
        var member = Tok(n.Arg4);
        var decays = false;
        if ((structType.Unqualified as CType.Named)?.Name is { } canon
            && _structFields.TryGetValue(canon, out var fields))
        {
            foreach (var f in fields)
            {
                if (f.Name != member) { continue; }
                decays = f.Type.Unqualified is CType.Array a && CodeGen.IsFixedBufferType(a.Element.CsType);
                break;
            }
        }
        return new OffsetOf(structType, member, decays) { Type = CType.SizeT };
    }

    private CExpr BuildStr(Item it)
    {
        var segs = CollectStrSegments(((C.Str)it.Content).Arg0);
        // A string literal has type char[N] (N = decoded bytes incl. NUL). It
        // decays to char* in most contexts — but NOT under sizeof, which is why
        // the array type is carried rather than the decayed pointer. The lowered
        // C# (byte*) is identical either way, so value uses are unaffected.
        var expr = DotCC.EmitHelpers.EncodeStringLiteral(segs, out var byteLen);
        return new LitStr(expr) { Type = new CType.Array(CType.Char, byteLen) };
    }

    /// <summary>The constant array bound of a char-array string declarator's
    /// <c>ArrDims</c> (single dimension, constant-folded).</summary>
    private int CharArrSize(Item arrDims)
    {
        var dims = BuildArrDims(arrDims);
        if (dims.Count != 1 || ConstEval(dims[0]) is not { } n)
        {
            throw new IrUnsupportedException("char-array string init with a non-constant or multi-dimensional bound");
        }
        return (int)n;
    }

    /// <summary>Collect adjacent string-literal segments (raw quoted lexemes) of a
    /// <c>StringSeq</c>, in source order.</summary>
    private List<string> CollectStrSegments(Item strSeq)
    {
        var segs = new List<string>();
        void Walk(Item node)
        {
            switch (node.Content)
            {
                case C.StrSeqCons sc: Walk(sc.Arg0); segs.Add(Tok(sc.Arg1)); break;
                case C.StrSeqOne so: segs.Add(Tok(so.Arg0)); break;
                default: segs.Add(Tok(node)); break;
            }
        }
        Walk(strSeq);
        return segs;
    }

    /// <summary><c>char s[] = "hi";</c> — a mutable char array initialised from a
    /// string. Lowers to a byte stackalloc of the decoded bytes plus the NUL;
    /// an explicit size zero-pads (or, exact-fit, may drop the NUL — C's rule).</summary>
    private CStmt BuildDeclCharArrStr(Item typeItem, Item nameItem, int? explicitSize, Item strSeqItem)
    {
        var elem = ResolveType(typeItem);
        var bytes = DotCC.EmitHelpers.StringByteValues(CollectStrSegments(strSeqItem));
        bytes.Add(0);                                   // NUL
        var count = explicitSize ?? bytes.Count;
        var inits = new List<CExpr>(count);
        for (var i = 0; i < count; i++)
        {
            var v = i < bytes.Count ? bytes[i] : 0;     // zero-pad beyond the string
            inits.Add(new LitInt(v.ToString(System.Globalization.CultureInfo.InvariantCulture), v) { Type = CType.Int });
        }
        var sym = _symbols.Declare(new Symbol
        {
            Name = Tok(nameItem), Kind = SymKind.Var, Type = new CType.Array(elem, count), Storage = Storage.Auto,
        });
        return new ArrayDecl(sym, elem, null, inits);
    }

    private CExpr BuildChr(C.Chr c)
    {
        // A C character constant has type int; emit its integer value (the simplest
        // C-faithful lowering — `'A'` → `65`, `'\n'` → `10`). The value carries into
        // byte/int sinks via C#'s constant conversions.
        var raw = Tok(c.Arg0);
        if (raw is null || raw.Length < 3) { return new LitInt("0", 0) { Type = CType.Int }; }
        var value = DecodeCharConstant(raw[1..^1]);
        return new LitInt(value.ToString(System.Globalization.CultureInfo.InvariantCulture), value) { Type = CType.Int };
    }

    /// <summary>Decode the body of a C character constant (the chars between the
    /// quotes) to its integer value: a single char, a named escape, a
    /// <c>\xHH</c> hex escape, or a <c>\NNN</c> octal escape.</summary>
    private static int DecodeCharConstant(string inner)
    {
        if (inner.Length == 0) { return 0; }
        if (inner[0] != '\\') { return inner[0]; }
        var esc = inner[1];
        switch (esc)
        {
            case 'n': return 10;
            case 't': return 9;
            case 'r': return 13;
            case 'a': return 7;
            case 'b': return 8;
            case 'f': return 12;
            case 'v': return 11;
            case '0' when inner.Length == 2: return 0;
            case '\\': return 92;
            case '\'': return 39;
            case '"': return 34;
            case '?': return 63;
            case 'x':
                return Convert.ToInt32(inner[2..], 16);
            case >= '0' and <= '7':
                return Convert.ToInt32(inner[1..], 8);
            default:
                throw new IrUnsupportedException("char literal '" + inner + "'");
        }
    }

    private CExpr BuildNum(C.Num n)
    {
        var raw = Tok(n.Arg0);
        if (raw.Contains('\'')) { Gate(2023, "digit separator", n.Arg0); }
        if (raw.Length >= 2 && raw[0] == '0' && raw[1] is 'b' or 'B') { Gate(2023, "binary integer literal", n.Arg0); }
        // Strip C integer suffix (u/U/l/L).
        var end = raw.Length;
        int ls = 0; var hasU = false;
        while (end > 0 && raw[end - 1] is 'u' or 'U' or 'l' or 'L')
        {
            if (raw[end - 1] is 'l' or 'L') { ls++; } else { hasU = true; }
            end--;
        }
        var digits = raw[..end].Replace("'", "");
        var suffix = (hasU, ls) switch { (false, 0) => "", (true, 0) => "u", (false, _) => "L", _ => "UL" };
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string text; long? val = null;
        if (digits.Length >= 2 && digits[0] == '0' && digits[1] is 'x' or 'X')
        {
            text = digits + suffix;
            if (long.TryParse(digits[2..], System.Globalization.NumberStyles.HexNumber, inv, out var hv)) { val = hv; }
        }
        else if (digits.Length >= 2 && digits[0] == '0' && digits[1] is 'b' or 'B')
        {
            text = digits + suffix;
            try { val = Convert.ToInt64(digits[2..], 2); } catch { }
        }
        else if (digits.Length >= 2 && digits[0] == '0')
        {
            var value = Convert.ToUInt64(digits, 8);
            text = value.ToString(inv) + suffix;
            if (value <= long.MaxValue) { val = (long)value; }
        }
        else
        {
            text = digits + suffix;
            if (long.TryParse(digits, inv, out var dv)) { val = dv; }
        }
        var ct = ls > 0 ? (hasU ? CType.ULong : CType.Long) : (hasU ? CType.UInt : CType.Int);
        return new LitInt(text, val) { Type = ct };
    }

    /// <summary>Build a floating-point literal, gating the C99 hex-float form
    /// (<c>0x1.8p3</c>) under an older -std=.</summary>
    private CExpr BuildFloat(C.Flt f)
    {
        var raw = Tok(f.Arg0);
        if (raw.Length >= 2 && raw[0] == '0' && raw[1] is 'x' or 'X') { Gate(1999, "hex float literal", f.Arg0); }
        return new LitFloat(LowerFloat(raw)) { Type = CType.Double };
    }

    private static string LowerFloat(string raw)
    {
        // C99 hex float literal (`0x1.8p3`, value = mantissa * 2^exp). C# has no
        // hex-float syntax, so parse the value and emit a round-trippable decimal.
        if (raw.Length > 2 && raw[0] == '0' && raw[1] is 'x' or 'X')
        {
            return LowerHexFloat(raw);
        }
        var last = raw.Length > 0 ? raw[^1] : '\0';
        if (last is 'f' or 'F') { return raw; }          // C# accepts the f suffix
        if (last is 'l' or 'L') { return raw[..^1]; }    // long double → double, drop L
        return raw;
    }

    /// <summary>Parse a C99 hexadecimal floating constant (<c>0xH.HHp±E</c>) to its
    /// exact binary value and render it as a round-trippable C# decimal literal
    /// (with the <c>f</c> suffix preserved for a float constant). All bits are
    /// computed from the hex mantissa and binary exponent, so an exactly
    /// representable source value lowers bit-for-bit identically.</summary>
    private static string LowerHexFloat(string raw)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var s = raw;
        var isFloat = false;
        var last = s[^1];
        if (last is 'f' or 'F') { isFloat = true; s = s[..^1]; }
        else if (last is 'l' or 'L') { s = s[..^1]; }

        var body = s[2..];                                // strip the 0x prefix
        var pIdx = body.IndexOfAny(new[] { 'p', 'P' });
        var mant = pIdx >= 0 ? body[..pIdx] : body;
        var exp = pIdx >= 0 ? int.Parse(body[(pIdx + 1)..], inv) : 0;

        var dot = mant.IndexOf('.');
        var intPart = dot < 0 ? mant : mant[..dot];
        var fracPart = dot < 0 ? "" : mant[(dot + 1)..];

        double value = intPart.Length > 0 ? Convert.ToUInt64(intPart, 16) : 0;
        var scale = 1.0 / 16.0;
        foreach (var ch in fracPart)
        {
            value += Convert.ToInt32(ch.ToString(), 16) * scale;
            scale /= 16.0;
        }
        value *= Math.Pow(2, exp);

        return isFloat
            ? ((float)value).ToString("R", inv) + "f"
            : value.ToString("R", inv);
    }

    // ---- helpers ---------------------------------------------------------

    private static string Tok(Item it) => it.Content as string
        ?? throw new IrUnsupportedException("expected terminal, got " + TypeName(it.Content));

    private static string TypeName(object? content) =>
        content is null ? "<null>" : content.GetType().Name;
}
