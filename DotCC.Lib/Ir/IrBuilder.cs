#nullable enable

using System;
using System.Collections.Generic;
using Item = global::LALR.CC.LexicalGrammar.Item;

namespace DotCC.Ir;

/// <summary>Thrown when the IR builder meets a parse-tree node it doesn't yet
/// lower. Carries the node type name so the <c>--ir</c> backend fails loudly on
/// an out-of-slice construct (and so such a fixture stays off the IR allow-list)
/// rather than silently miscompiling.</summary>
public sealed class IrUnsupportedException : Exception
{
    public IrUnsupportedException(string node) : base($"--ir backend does not yet support: {node}") { }
}

/// <summary>
/// Builds the typed IR from the raw LALR parse tree (driven by
/// <see cref="ParseTreeIdentityVisitor"/>). A TOP-DOWN recursive walk with full
/// scope/type context — the opposite of the legacy bottom-up string emitter.
/// Phase 0 covers a vertical slice: functions, scalar/pointer types, arithmetic
/// / relational / logical / assignment expressions, calls (incl. printf), and
/// if/while/for/return/block. Out-of-slice nodes raise
/// <see cref="IrUnsupportedException"/>.
/// </summary>
internal sealed class IrBuilder
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
    private readonly HashSet<string> _emittedTypes = new(StringComparer.Ordinal);
    private string _file = "";

    public List<FuncDef> Functions { get; } = new();
    public List<GlobalVar> Globals { get; } = new();
    public List<StructTypeDef> Types { get; } = new();
    public List<Diagnostic> Diagnostics { get; } = new();

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
        switch (it.Content)
        {
            case C.FnsCons c: FlattenFns(c.Arg0, onFn); FlattenFns(c.Arg1, onFn); break;
            case C.FnsOne o: FlattenFns(o.Arg0, onFn); break;
            default: onFn(it); break;
        }
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
            // `static T x = { … };` at file scope — a once-initialised struct/union field.
            case C.GlobalStaticStructInit g: BuildGlobalStructInit(g.Arg1, g.Arg2, g.Arg5); break;
            // `extern T x;` declares the name + type for resolution but emits no
            // field — the definition lives in another TU (dotcc whole-program model).
            case C.ExternVarDecl g: BuildGlobalDecls(g.Arg1, g.Arg2, Storage.Extern); break;
            // `typedef <type> <name>;` — record name → underlying type. Resolution
            // (ResolveType's TypeName case) then sees through it everywhere.
            case C.TypedefAlias t: _typedefs[Tok(t.Arg2)] = ResolveType(t.Arg1); break;
            // enum definitions — register the enumerators as integer constants.
            case C.EnumDef e: RegisterEnum(e.Arg3); break;
            case C.EnumDefTyped e: RegisterEnum(e.Arg5); break;
            case C.TypedefEnum e: RegisterEnum(e.Arg4); _typedefs[Tok(e.Arg6)] = CType.Int; break;
            case C.TypedefEnumAnon e: RegisterEnum(e.Arg3); _typedefs[Tok(e.Arg5)] = CType.Int; break;
            // struct/union definitions.
            case C.StructDef s: BuildStructDef(Tok(s.Arg1), s.Arg3, null, isUnion: false); break;
            case C.UnionDef s: BuildStructDef(Tok(s.Arg1), s.Arg3, null, isUnion: true); break;
            case C.TypedefStruct s: BuildStructDef(Tok(s.Arg2), s.Arg4, Tok(s.Arg6), isUnion: false); break;
            case C.TypedefUnion s: BuildStructDef(Tok(s.Arg2), s.Arg4, Tok(s.Arg6), isUnion: true); break;
            case C.TypedefStructAnon s: BuildStructDef(null, s.Arg3, Tok(s.Arg5), isUnion: false); break;
            case C.TypedefUnionAnon s: BuildStructDef(null, s.Arg3, Tok(s.Arg5), isUnion: true); break;
            // `struct Tag;` forward declaration — C# resolves order-independently.
            case C.StructFwd: break;
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
        WalkDeclList(ResolveType(typeItem), listItem, (name, initItem, type) =>
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

    /// <summary>Register an enum's enumerators as integer constants. Values
    /// auto-increment from the previous (starting at 0), with explicit
    /// <c>= expr</c> overriding (expr must be an integer constant expression).
    /// dotcc lowers C enums to plain ints, so the enum tag itself is not a C#
    /// type — references to an enumerator emit its literal value.</summary>
    private void RegisterEnum(Item enumList)
    {
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
                Name = name, Kind = SymKind.EnumConst, Type = CType.Int, ConstValue = next, IsGlobal = true,
            });
            next++;
        }
        Walk(enumList);
    }

    /// <summary>Evaluate an integer constant expression, or null if non-constant.
    /// Covers the forms enum initializers and array bounds use (literals,
    /// unary +/-/~, the binary arithmetic/shift/bitwise operators, parens,
    /// integer casts, and already-resolved enum-constant literals).</summary>
    private static long? ConstEval(CExpr e) => e switch
    {
        LitInt i => i.Value,
        SizeOfExpr s => s.Of.SizeOf,
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

    private static long? ApplyConstBin(BinOp op, long l, long r) => op switch
    {
        BinOp.Add => l + r, BinOp.Sub => l - r, BinOp.Mul => l * r,
        BinOp.Div => r != 0 ? l / r : null, BinOp.Mod => r != 0 ? l % r : null,
        BinOp.Shl => l << (int)r, BinOp.Shr => l >> (int)r,
        BinOp.BitAnd => l & r, BinOp.BitOr => l | r, BinOp.BitXor => l ^ r,
        _ => null,
    };

    // ---- structs / unions ------------------------------------------------

    /// <summary>Define a struct/union. <paramref name="tag"/> is the C tag (null
    /// for an anonymous <c>typedef struct {…} Alias</c>); <paramref name="alias"/>
    /// the typedef name if any. Emits a C# struct under a canonical name, records
    /// its fields for member-type resolution, and maps the typedef alias to it.</summary>
    private void BuildStructDef(string? tag, Item memberList, string? alias, bool isUnion)
    {
        var canonical = tag ?? alias ?? throw new IrUnsupportedException("struct with neither tag nor typedef name");
        var fields = BuildStructFields(memberList);
        if (_emittedTypes.Add(canonical))
        {
            _structFields[canonical] = fields;
            Types.Add(new StructTypeDef(canonical, fields, isUnion));
        }
        // `struct Tag` and the typedef alias both resolve to the canonical type.
        if (alias is not null) { _typedefs[alias] = new CType.Named(canonical); }
    }

    /// <summary>Parse a struct/union member list into (field, type) pairs. Scalar
    /// and pointer members are supported; array / bit-field / anonymous members
    /// are deferred (raise <see cref="IrUnsupportedException"/>).</summary>
    private List<StructField> BuildStructFields(Item memberList)
    {
        var fields = new List<StructField>();
        void Member(Item m)
        {
            switch (m.Content)
            {
                case C.MembersCons c: Member(c.Arg0); Member(c.Arg1); break;
                case C.MembersOne o: Member(o.Arg0); break;
                case C.StructMemberList sm:
                    WalkDeclList(ResolveType(sm.Arg0), sm.Arg1, (name, _, type) => fields.Add(new StructField(name, type)));
                    break;
                default: throw new IrUnsupportedException(TypeName(m.Content));
            }
        }
        Member(memberList);
        return fields;
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
        C.TypeFromSpec t => ResolveSpecs(CollectSpecs(t.Arg0)),
        C.TypePtr t => new CType.Pointer(ResolveType(t.Arg0)),
        C.TypePtrQualConst t => new CType.Pointer(ResolveType(t.Arg0)),
        C.TypePtrQualVolatile t => new CType.Pointer(ResolveType(t.Arg0)),
        C.TypePtrQualRestrict t => new CType.Pointer(ResolveType(t.Arg0)),
        C.TypeName t => ResolveTypeName(Tok(t.Arg0)),
        // `enum Tag` as a type — dotcc lowers enums to plain int.
        C.TypeEnum => CType.Int,
        // `struct Tag` / `union Tag` as a type — the canonical C# struct name.
        C.TypeStruct t => new CType.Named(Tok(t.Arg1)),
        C.TypeUnion t => new CType.Named(Tok(t.Arg1)),
        // Inline anonymous aggregate used as a type — `union { int i; float f; } u;`
        // (a NAMED member/var of an unnamed aggregate). Synthesize a struct name.
        C.TypeAnonStruct t => ResolveAnonAggregate(it, t.Arg3, isUnion: false),
        C.TypeAnonUnion t => ResolveAnonAggregate(it, t.Arg3, isUnion: true),
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
        var fields = BuildStructFields(memberListItem);
        _structFields[name] = fields;
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
    private static CType ResolveSpecs(List<string> specs)
    {
        int u = 0, s = 0, sh = 0, lng = 0;
        var quals = TypeQual.None;
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
                case "inline" or "_Noreturn" or "_Complex": break; // ignored for type purposes here
                case "void": base_ = "void"; break;
                case "char": base_ = "char"; break;
                case "int": base_ = "int"; break;
                case "float": base_ = "float"; break;
                case "double": base_ = "double"; break;
                case "_Bool": base_ = "_Bool"; break;
                case "Float128": base_ = "Float128"; break;
            }
        }
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
                FlattenStmts(b.Arg2, x => stmts.Add(BuildStmt(x)));
                _symbols.ExitScope();
                break;
            case C.BlockEmpty:
                break;
            default:
                throw new IrUnsupportedException(TypeName(it.Content));
        }
        return new Block(stmts);
    }

    private void FlattenStmts(Item it, Action<Item> onStmt)
    {
        switch (it.Content)
        {
            case C.StmtsCons c: onStmt(c.Arg0); FlattenStmts(c.Arg1, onStmt); break;
            case C.StmtsOne o: onStmt(o.Arg0); break;
            default: onStmt(it); break;
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
            case C.StmtGoto s: return new Goto(DotCC.CSharpEmitter.Id(Tok(s.Arg1))) { Pos = pos };
            case C.StmtLabel s: return new Labeled(DotCC.CSharpEmitter.Id(Tok(s.Arg0)), BuildStmt(s.Arg2)) { Pos = pos };
            // A block-scope enum definition has no storage — register its
            // constants and emit nothing (an empty block).
            case C.StmtEnumDef s: RegisterEnum(s.Arg3); return new Block(System.Array.Empty<CStmt>()) { Pos = pos };
            case C.StmtEnumDefTyped s: RegisterEnum(s.Arg5); return new Block(System.Array.Empty<CStmt>()) { Pos = pos };
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
        C.DeclStructInit d => BuildDeclStructInit(d),
        C.DeclArr d => BuildArrDecl(d.Arg0, d.Arg1, d.Arg2, null, implicitSize: false),
        C.DeclArrEmptyInit d => BuildArrDecl(d.Arg0, d.Arg1, d.Arg2, null, implicitSize: false),
        C.DeclArrInit d => BuildArrDecl(d.Arg0, d.Arg1, d.Arg2, d.Arg5, implicitSize: false),
        C.DeclArrInitImplicit d => BuildArrDecl(d.Arg0, d.Arg1, null, d.Arg6, implicitSize: true),
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
        WalkDeclList(ResolveType(n.Arg1), n.Arg2, (name, initItem, type) =>
        {
            var sym = new Symbol
            {
                Name = name, Kind = SymKind.Var, Type = type,
                Storage = Storage.Static, IsGlobal = true,
                CsName = $"{DotCC.CSharpEmitter.Id(name)}__s{_staticLocalSeq++}",
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
            CsName = $"{DotCC.CSharpEmitter.Id(Tok(n.Arg2))}__s{_staticLocalSeq++}",
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
        var entries = new List<LocalDecl>();
        WalkDeclList(ResolveType(decl.Arg0), decl.Arg1, (name, initItem, type) =>
        {
            var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = type, Storage = Storage.Auto });
            entries.Add(new LocalDecl(sym, initItem is { } ii ? BuildExpr(ii) : null));
        });
        return new DeclStmt(entries);
    }

    /// <summary>Walk a comma-separated init-declarator list, invoking
    /// <paramref name="add"/> with each declarator's (name, initializer?, type).
    /// The FIRST declarator's <c>*</c>s were greedily folded into
    /// <paramref name="baseType"/> by the grammar's <c>Type → Type *</c> rule;
    /// each subsequent declarator rebuilds its type from the pointer-stripped
    /// element plus its own <c>*</c>s (so <c>int *a, b;</c> ⇒ a:int*, b:int).
    /// Shared by local (<see cref="BuildDecl"/>) and file-scope declarations.</summary>
    private void WalkDeclList(CType baseType, Item listItem, Action<string, Item?, CType> add)
    {
        var element = baseType;
        while (element is CType.Pointer p) { element = p.Pointee; }
        void WalkTail(Item it, int stars)
        {
            switch (it.Content)
            {
                case C.DeclItemTailPtr p: WalkTail(p.Arg1, stars + 1); break;
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

    /// <summary>A local array declaration. Lowers to a C# <c>stackalloc</c>. A
    /// constant dimension types the symbol as <see cref="CType.Array"/> (so
    /// <c>sizeof(arr)</c> and the array-length idiom resolve); a runtime extent
    /// (VLA-ish) decays to a pointer. Multi-dimensional arrays are deferred.</summary>
    private ArrayDecl BuildArrDecl(Item typeItem, Item nameItem, Item? dimsItem, Item? initItem, bool implicitSize)
    {
        var elem = ResolveType(typeItem);
        var name = Tok(nameItem);
        var inits = initItem is { } ii ? BuildInitList(ii) : null;

        var dims = dimsItem is { } di ? BuildArrDims(di) : new List<CExpr>();
        if (dims.Count > 1) { throw new IrUnsupportedException("multi-dimensional array"); }
        var dim = dims.Count == 1 ? dims[0] : null;

        CType arrType;
        CExpr? countExpr = null;
        if (dim is not null && ConstEval(dim) is { } cnt)
        {
            // A constant-expression bound (a literal, or a folded ICE like
            // `sizeof(long) * CHAR_BIT`) is a fixed-size array — sizeof(arr) and
            // the array-length idiom need the count on the type.
            arrType = new CType.Array(elem, (int)cnt);
            countExpr = dim;
        }
        else if (inits is not null)
        {
            // Brace-initialized, dimension implicit or non-constant → count = #elems.
            arrType = new CType.Array(elem, inits.Count);
        }
        else if (dim is not null)
        {
            // Runtime extent (VLA): C arrays decay to a pointer in value context.
            arrType = new CType.Pointer(elem);
            countExpr = dim;
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

    /// <summary>Flatten a brace initializer list to its scalar element
    /// expressions. Nested / designated initializers are deferred.</summary>
    private List<CExpr> BuildInitList(Item it)
    {
        var items = new List<CExpr>();
        void Walk(Item n)
        {
            switch (n.Content)
            {
                case C.InitListCons c: Walk(c.Arg0); items.Add(BuildInitElem(c.Arg2)); break;
                case C.InitListTrail t: Walk(t.Arg0); break; // trailing comma — no element
                case C.InitListOne o: items.Add(BuildInitElem(o.Arg0)); break;
                default: items.Add(BuildInitElem(n)); break;
            }
        }
        Walk(it);
        return items;
    }

    private CExpr BuildInitElem(Item it) => it.Content switch
    {
        C.InitElemExpr e => BuildExpr(e.Arg0),
        _ => throw new IrUnsupportedException(TypeName(it.Content)),
    };

    /// <summary><c>struct Point p = {3, 4};</c> — a local declaration with a
    /// positional aggregate initializer. Types the symbol as the struct and
    /// builds a <see cref="StructInit"/> from the brace list.</summary>
    private CStmt BuildDeclStructInit(C.DeclStructInit n)
    {
        var type = ResolveType(n.Arg0);
        var init = BuildAggregateInit(type, n.Arg4);
        var sym = _symbols.Declare(new Symbol { Name = Tok(n.Arg1), Kind = SymKind.Var, Type = type, Storage = Storage.Auto });
        return new DeclStmt(new[] { new LocalDecl(sym, init) });
    }

    /// <summary>Build a positional struct/union aggregate initializer: zip the
    /// brace-list elements onto the struct's fields in declaration order, a
    /// nested brace recursing into a struct/union-typed field. A short list
    /// leaves the trailing fields unset (their zero default — C's partial init).</summary>
    private StructInit BuildAggregateInit(CType type, Item initListItem)
    {
        var canonical = (type.Unqualified as CType.Named)?.Name
            ?? throw new IrUnsupportedException("aggregate initializer for a non-struct type");
        if (!_structFields.TryGetValue(canonical, out var fields))
        {
            throw new IrUnsupportedException($"aggregate initializer for unknown struct/union '{canonical}'");
        }
        var elems = CollectInitElems(initListItem);
        var members = new List<FieldInit>(System.Math.Min(elems.Count, fields.Count));
        for (var i = 0; i < elems.Count && i < fields.Count; i++)
        {
            var field = fields[i];
            CExpr value = elems[i].Content is C.InitElemNest nest
                ? BuildAggregateInit(field.Type, nest.Arg1)   // nested brace → struct/union field
                : BuildInitElem(elems[i]);
            members.Add(new FieldInit(field.Name, field.Type, value));
        }
        return new StructInit(members) { Type = type };
    }

    /// <summary>Collect a brace-init list's elements as raw parse items (so the
    /// caller can distinguish a scalar element from a nested <c>{ … }</c> group),
    /// in source order.</summary>
    private static List<Item> CollectInitElems(Item it)
    {
        var elems = new List<Item>();
        void Walk(Item n)
        {
            switch (n.Content)
            {
                case C.InitListCons c: Walk(c.Arg0); elems.Add(c.Arg2); break;
                case C.InitListTrail t: Walk(t.Arg0); break;
                case C.InitListOne o: elems.Add(o.Arg0); break;
                default: elems.Add(n); break;
            }
        }
        Walk(it);
        return elems;
    }

    private For BuildForDecl(C.StmtForDecl s)
    {
        // for ( <decl> ; <cond> ; <post> ) <body> — Arg2 is the ScopeEnter epsilon;
        // Decl=Arg3, ForCond=Arg5, ForPost=Arg7, body=Arg9 (see legacy StmtForDecl).
        _symbols.EnterScope();
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
            C.Flt f => new LitFloat(LowerFloat(Tok(f.Arg0))) { Type = CType.Double },
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
            C.Call c => BuildCall(c.Arg0, c.Arg2),
            C.CallNoArgs c => BuildCall(c.Arg0, null),
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
        // Result type: arithmetic arms reconcile per the usual conversions; else
        // take the then-arm's type (good enough until the coercion pass lands).
        var ty = then.Type.IsArithmetic && els.Type.IsArithmetic
            ? CType.UsualArithmetic(then.Type, els.Type) : then.Type;
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

    private CExpr BuildMember(Item baseItem, Item fieldItem, bool arrow)
    {
        var base_ = BuildExpr(baseItem);
        var field = Tok(fieldItem);
        return new Member(base_, field, arrow) { Type = MemberType(base_, field), IsLValue = true };
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
        CType t = op switch
        {
            UnOp.LogNot => CType.Int,
            UnOp.AddrOf => new CType.Pointer(oe.Type),
            UnOp.Deref => oe.Type is CType.Pointer p ? p.Pointee : oe.Type,
            _ => oe.Type,
        };
        return new Unary(op, oe) { Type = t, IsLValue = op == UnOp.Deref };
    }

    private CExpr BuildVar(C.Var v)
    {
        var name = Tok(v.Arg0);
        var sym = _symbols.Resolve(name);
        if (sym is { Kind: SymKind.EnumConst })
        {
            // An enum constant lowers to its literal integer value.
            return new LitInt(sym.ConstValue.ToString(System.Globalization.CultureInfo.InvariantCulture), sym.ConstValue) { Type = CType.Int };
        }
        if (sym is not null)
        {
            return new VarRef(sym) { Type = sym.Type, IsLValue = sym.Kind is SymKind.Var or SymKind.Param };
        }
        // Unresolved (a macro-substituted token, a builtin not in a header). Emit
        // the escaped raw name verbatim and let Roslyn arbitrate. Slice code never
        // hits this; it's a safety net during incremental growth.
        return new Raw(DotCC.CSharpEmitter.Id(name)) { Type = CType.Int };
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
        // `tbl[i](x)`, `s.fn(x)`. `*fp` is a C no-op on a function pointer (C#
        // calls it directly and rejects the deref), so peel a leading deref.
        var callee = BuildExpr(calleeItem);
        if (callee is Unary { Op: UnOp.Deref } u) { callee = u.Operand; }
        var rty = callee.Type switch
        {
            CType.Func f2 => f2.Return,
            CType.Pointer { Pointee: CType.Func f3 } => f3.Return,
            _ => CType.Int,
        };
        return new IndirectCall(callee, args) { Type = rty };
    }

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

    private CExpr BuildStr(Item it)
    {
        var c = (C.Str)it.Content;
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
        Walk(c.Arg0);
        // A string literal has type char[N] (N = decoded bytes incl. NUL). It
        // decays to char* in most contexts — but NOT under sizeof, which is why
        // the array type is carried rather than the decayed pointer. The lowered
        // C# (byte*) is identical either way, so value uses are unaffected.
        var expr = DotCC.CSharpEmitter.EncodeStringLiteral(segs, out var byteLen);
        return new LitStr(expr) { Type = new CType.Array(CType.Char, byteLen) };
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

    private static string LowerFloat(string raw)
    {
        var last = raw.Length > 0 ? raw[^1] : '\0';
        if (last is 'f' or 'F') { return raw; }          // C# accepts the f suffix
        if (last is 'l' or 'L') { return raw[..^1]; }    // long double → double, drop L
        return raw;
    }

    // ---- helpers ---------------------------------------------------------

    private static string Tok(Item it) => it.Content as string
        ?? throw new IrUnsupportedException("expected terminal, got " + TypeName(it.Content));

    private static string TypeName(object? content) =>
        content is null ? "<null>" : content.GetType().Name;
}
