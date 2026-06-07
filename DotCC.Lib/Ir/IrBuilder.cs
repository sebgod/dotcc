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
    private string _file = "";

    public List<FuncDef> Functions { get; } = new();
    public List<GlobalVar> Globals { get; } = new();
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
            default: throw new IrUnsupportedException(TypeName(fn.Content));
        }
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

    private readonly record struct FnSig(CType Return, string Name, List<(CType Type, string Name)> Params, bool Variadic, bool IsStatic);

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

    private List<(CType, string)> BuildParams(Item paramList, out bool variadic)
    {
        var acc = new List<(CType, string)>();
        var vararg = false;
        var unnamed = 0;
        void Walk(Item it)
        {
            switch (it.Content)
            {
                case C.ParamsCons c: Walk(c.Arg0); Walk(c.Arg2); break;
                case C.ParamsOne o: Walk(o.Arg0); break;
                case C.ParamsVararg v: Walk(v.Arg0); vararg = true; break;
                case C.Param p: acc.Add((ResolveType(p.Arg0), Tok(p.Arg1))); break;
                case C.ParamUnnamed p: acc.Add((ResolveType(p.Arg0), "_p" + unnamed++)); break;
                case C.ParamArrayUnsized p: acc.Add((new CType.Pointer(ResolveType(p.Arg0)), Tok(p.Arg1))); break;
                case C.ParamArraySized p: acc.Add((new CType.Pointer(ResolveType(p.Arg0)), Tok(p.Arg1))); break;
                default: throw new IrUnsupportedException(TypeName(it.Content));
            }
        }
        Walk(paramList);
        variadic = vararg;
        return acc;
    }

    // ---- types -----------------------------------------------------------

    private CType ResolveType(Item it) => it.Content switch
    {
        C.TypeFromSpec t => ResolveSpecs(CollectSpecs(t.Arg0)),
        C.TypePtr t => new CType.Pointer(ResolveType(t.Arg0)),
        C.TypePtrQualConst t => new CType.Pointer(ResolveType(t.Arg0)),
        C.TypePtrQualVolatile t => new CType.Pointer(ResolveType(t.Arg0)),
        C.TypePtrQualRestrict t => new CType.Pointer(ResolveType(t.Arg0)),
        C.TypeName t => new CType.Named(Tok(t.Arg0)),
        _ => throw new IrUnsupportedException(TypeName(it.Content)),
    };

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
            case C.StmtDecl d: return BuildDecl(d.Arg0) with { Pos = pos };
            case C.StmtExpr e: return new ExprStmt(BuildExpr(e.Arg0)) { Pos = pos };
            case C.StmtEmpty: return new Block(System.Array.Empty<CStmt>()) { Pos = pos };
            case C.StmtIf s: return new If(BuildExpr(s.Arg2), BuildStmt(s.Arg4), null) { Pos = pos };
            case C.StmtIfElse s: return new If(BuildExpr(s.Arg2), BuildStmt(s.Arg4), BuildStmt(s.Arg6)) { Pos = pos };
            case C.StmtWhile s: return new While(BuildExpr(s.Arg2), BuildStmt(s.Arg4)) { Pos = pos };
            case C.StmtReturn s: return new Return(BuildExpr(s.Arg1)) { Pos = pos };
            case C.StmtReturnVoid: return new Return(null) { Pos = pos };
            case C.StmtBreak: return new Break { Pos = pos };
            case C.StmtContinue: return new Continue { Pos = pos };
            case C.StmtForDecl s: return BuildForDecl(s) with { Pos = pos };
            default: throw new IrUnsupportedException(TypeName(it.Content));
        }
    }

    private DeclStmt BuildDecl(Item declItem)
    {
        if (declItem.Content is not C.Decl decl) { throw new IrUnsupportedException(TypeName(declItem.Content)); }
        var baseType = ResolveType(decl.Arg0);
        var entries = new List<LocalDecl>();
        void Walk(Item it)
        {
            switch (it.Content)
            {
                case C.DeclItemListCons c: Walk(c.Arg0); Walk(c.Arg2); break;
                case C.DeclItemListOne o: Walk(o.Arg0); break;
                case C.DeclItem di: AddEntry(Tok(di.Arg0), null); break;
                case C.DeclItemInit di: AddEntry(Tok(di.Arg0), di.Arg2); break;
                default: throw new IrUnsupportedException(TypeName(it.Content));
            }
        }
        void AddEntry(string name, Item? initItem)
        {
            var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = baseType, Storage = Storage.Auto });
            var init = initItem is { } ii ? BuildExpr(ii) : null;
            entries.Add(new LocalDecl(sym, init));
        }
        Walk(decl.Arg1);
        return new DeclStmt(entries);
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
        C.ForPostExprs e => BuildForPostExpr(e.Arg0),
        _ => throw new IrUnsupportedException(TypeName(it.Content)),
    };

    private CExpr BuildForPostExpr(Item it) => it.Content switch
    {
        C.CommaExprOne o => BuildExpr(o.Arg0),
        _ => throw new IrUnsupportedException(TypeName(it.Content)), // multi-expr post: later phase
    };

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
            C.PreInc u => Un(UnOp.PreInc, u.Arg1),
            C.PreDec u => Un(UnOp.PreDec, u.Arg1),
            C.PostInc u => Un(UnOp.PostInc, u.Arg0),
            C.PostDec u => Un(UnOp.PostDec, u.Arg0),
            C.Neg u => Un(UnOp.Neg, u.Arg1),
            C.BNot u => Un(UnOp.BitNot, u.Arg1),
            C.LNot u => Un(UnOp.LogNot, u.Arg1),
            C.Call c => BuildCall(c.Arg0, c.Arg2),
            C.CallNoArgs c => BuildCall(c.Arg0, null),
            _ => throw new IrUnsupportedException(TypeName(it.Content)),
        };
        return e with { Pos = pos };
    }

    private CExpr Bin(BinOp op, Item l, Item r)
    {
        var le = BuildExpr(l);
        var re = BuildExpr(r);
        return new Binary(op, le, re) { Type = ArithType(le.Type, re.Type) };
    }

    private CExpr Rel(BinOp op, Item l, Item r) =>
        new Binary(op, BuildExpr(l), BuildExpr(r)) { Type = CType.Int };

    /// <summary>Simplified C usual arithmetic conversions: the wider/floating
    /// operand type wins; integer promotion to at least <c>int</c>. Enough for
    /// the slice — the full rule (incl. signedness ranking) lands with the
    /// coercion pass.</summary>
    private static CType ArithType(CType a, CType b)
    {
        bool Float(CType t) => t is CType.Prim { Integer: false };
        if (Float(a) || Float(b))
        {
            return (a.SizeOf >= 8 || b.SizeOf >= 8) ? CType.Double : CType.Float;
        }
        var wider = a.SizeOf >= b.SizeOf ? a : b;
        return wider.SizeOf >= 8 ? wider : CType.Int;
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
        var name = CalleeName(calleeItem);
        var args = new List<CExpr>();
        if (argList is { } al) { FlattenArgs(al, x => args.Add(BuildExpr(x))); }
        var sym = _symbols.Resolve(name);
        var ret = sym?.Type is CType.Func f ? f.Return : CType.Int;
        return new Call(name, args, IsPrintfFamily(name)) { Type = ret };
    }

    private string CalleeName(Item it) => it.Content switch
    {
        C.Var v => Tok(v.Arg0),
        C.Paren p => CalleeName(p.Arg1),
        _ => throw new IrUnsupportedException("call through " + TypeName(it.Content)),
    };

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
        return new LitStr(DotCC.CSharpEmitter.EncodeStringLiteral(segs)) { Type = CType.CharPtr };
    }

    private CExpr BuildChr(C.Chr c)
    {
        // C char constant has type int; our char is byte. Printable ASCII stays
        // readable, everything else lowers to its numeric byte value.
        var raw = Tok(c.Arg0);
        if (raw is null || raw.Length < 3) { return new LitInt("(byte)0", 0) { Type = CType.Int }; }
        var inner = raw[1..^1];
        // Slice-simple: single printable char or a one-char escape we map directly.
        string text = inner switch
        {
            "\\n" => "(byte)10", "\\t" => "(byte)9", "\\r" => "(byte)13",
            "\\0" => "(byte)0", "\\\\" => "(byte)92", "\\'" => "(byte)39",
            _ when inner.Length == 1 && inner[0] is >= ' ' and <= '~' => $"(byte)'{inner}'",
            _ => throw new IrUnsupportedException("char literal " + raw),
        };
        return new LitInt(text, null) { Type = CType.Int };
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
