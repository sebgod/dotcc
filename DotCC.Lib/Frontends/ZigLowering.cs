#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>
/// Lowers a parsed Zig translation unit (the raw <c>Zig.*</c> parse tree yielded by
/// the generated identity visitor) onto the neutral typed IR — the Zig-side analogue
/// of <see cref="IrBuilder.AddUnit"/>'s top-down walk over <c>C.*</c>. Standalone by
/// design: it reuses the neutral IR types (<see cref="FuncDef"/>/<see cref="CExpr"/>/
/// <see cref="CType"/>/<see cref="Symbol"/>/<see cref="SymbolTable"/>) and the target's
/// <see cref="INameLegalizer"/>, but leaves the C <see cref="IrBuilder"/> untouched, so
/// the two frontends stay decoupled. Shared IR-construction helpers get extracted from
/// <see cref="IrBuilder"/> only once this second implementer shows what's actually common.
///
/// SURFACE (grows deliberately; the grammar parses more than this lowers): functions
/// with parameters; typed/untyped <c>const</c>/<c>var</c>; <c>return</c>; <c>if</c>/
/// <c>while</c> statements and assignment; the <c>if</c>-expression (→ ternary); the
/// full arithmetic / comparison / boolean / bitwise / shift / prefix operator set; and
/// the fixed-width integer types mapped to their faithful C# signedness. Everything else
/// throws <see cref="IrUnsupportedException"/> — fail loudly, grow deliberately.
///
/// SEMANTICS NOTE: Zig has no C-style implicit promotions — a valid Zig binary op has
/// same-typed operands — but we reuse C's <see cref="CType.UsualArithmetic"/> for the
/// result type anyway, because the C# BACKEND promotes identically (<c>u8 + u8</c> is
/// <c>int</c> in C# too) and inserts the narrowing cast back at the typed sink. A
/// comparison / boolean op is typed <see cref="CType.Int"/>: the backend renders it as
/// an integer-valued <c>(CBool)(…)</c> and wraps every condition in <c>Cond.B(…)</c>,
/// so an <c>int</c>-typed relational feeds <c>if</c>/<c>while</c>/ternary cleanly.
/// </summary>
internal sealed class ZigLowering
{
    private readonly IrBuilder _ir;
    private readonly SymbolTable _symbols;

    /// <summary>The flat global error set: each distinct <c>error.Foo</c> name → a stable
    /// non-zero code (0 is the success sentinel). Shared across the units of one build (the
    /// caller passes one dictionary) so a given error name gets one code program-wide — V1
    /// erases the error SET, so a single space suffices. See <see cref="ErrorCode"/>.</summary>
    private readonly Dictionary<string, int> _errorCodes;

    /// <summary>The return type of the function whose body is currently being lowered (null
    /// outside a body / for a void-less return). When it is a <see cref="CType.ErrorUnion"/>,
    /// <c>return</c> wraps its value as an error union (<see cref="LowerReturn"/>).</summary>
    private CType? _currentFnRet;

    public ZigLowering(IrBuilder ir, INameLegalizer names, Dictionary<string, int>? errorCodes = null)
    {
        _ir = ir;
        _symbols = new SymbolTable(names);
        _errorCodes = errorCodes ?? new Dictionary<string, int>(System.StringComparer.Ordinal);
    }

    /// <summary>Resolve an error name to its stable code in the flat global error set,
    /// assigning the next 1-based code on first sight (0 is reserved for success).</summary>
    private int ErrorCode(string name)
    {
        if (!_errorCodes.TryGetValue(name, out var code))
        {
            code = _errorCodes.Count + 1;
            _errorCodes[name] = code;
        }
        return code;
    }

    private static string Tok(Item it) => it.Content as string
        ?? throw new IrUnsupportedException("expected a token lexeme");

    public void Lower(Item root)
    {
        // Two passes so a call can forward-reference: Zig has no prototypes, and a
        // function may call one defined later in the file. Pass 1 declares every
        // signature in the (global) scope; pass 2 lowers each body against them. An
        // `extern fn` prototype is declared in pass 1 too but has no body to lower.
        var entries = new List<(Symbol sym, List<(string name, CType type)> ps, Item body)>();
        foreach (var decl in Flatten(root))
        {
            var d = decl.Content is Zig.PubFn p ? p.Arg1 : decl;   // unwrap `pub`
            switch (d.Content)
            {
                case Zig.ExternFnProto f:       DeclareExternFn(f.Arg2, f.Arg4, f.Arg6); break;  // extern fn IDENT ( Params ) Type ;
                case Zig.ExternFnProtoNoArgs f: DeclareExternFn(f.Arg2, null, f.Arg5); break;     // extern fn IDENT ( ) Type ;
                case Zig.FnDef f:          entries.Add(DeclareFn(f.Arg1, f.Arg3, f.Arg5, f.Arg6)); break;
                case Zig.FnDefNoArgs f:    entries.Add(DeclareFn(f.Arg1, null, f.Arg4, f.Arg5)); break;
                case Zig.FnDefErr f:       entries.Add(DeclareFn(f.Arg1, f.Arg3, f.Arg6, f.Arg7, errUnion: true)); break;   // `!T` return → ErrorUnion(T)
                case Zig.FnDefNoArgsErr f: entries.Add(DeclareFn(f.Arg1, null, f.Arg5, f.Arg6, errUnion: true)); break;
                default: throw new IrUnsupportedException("zig top-level decl: " + (d.Content?.GetType().Name ?? "null"));
            }
        }
        foreach (var (sym, ps, body) in entries) { LowerFnBody(sym, ps, body); }
    }

    // ---- top level -------------------------------------------------------

    /// <summary>Pass 1: declare a function's signature (return + parameter types) in
    /// the global scope and bundle its body for pass 2. Declaring all signatures up
    /// front is what lets a call forward-reference a function defined later.</summary>
    private (Symbol sym, List<(string name, CType type)> ps, Item body) DeclareFn(
        Item nameTok, Item? paramsItem, Item retType, Item body, bool errUnion = false)
    {
        var ret = LowerType(retType);
        // A `!T` return (Zig's inferred error set) wraps the payload in an error union;
        // V1 erases the set, so the leading `!` just marks the union (see CType.ErrorUnion).
        if (errUnion) { ret = new CType.ErrorUnion(ret); }
        var paramInfos = CollectParamInfos(paramsItem, out var variadic);
        // Zig allows `...` ONLY in an extern prototype — a non-extern variadic fn is
        // a compile error. Reject it the same way (faithful to Zig; our subset has no
        // way to access varargs from a Zig body anyway).
        if (variadic)
        {
            throw new IrUnsupportedException(
                $"function '{Tok(nameTok)}': a non-extern Zig function cannot be variadic (use `extern fn`)");
        }

        var funcSym = _symbols.Declare(new Symbol
        {
            Name = Tok(nameTok),
            Kind = SymKind.Func,
            Type = new CType.Func(ret, paramInfos.Select(p => p.type).ToList(), false),
            IsGlobal = true,
        });
        return (funcSym, paramInfos, body);
    }

    /// <summary>Collect a parameter list's <c>(name, type)</c> infos in source order,
    /// detecting the variadic marker <c>...</c> (Zig's <c>DOT3</c> ParamDecl). The
    /// marker carries no name/type, so it is excluded from the infos and instead sets
    /// <paramref name="variadic"/>; it must be the LAST parameter (C / Zig both require
    /// the fixed params to precede the pack).</summary>
    private List<(string name, CType type)> CollectParamInfos(Item? paramsItem, out bool variadic)
    {
        variadic = false;
        var infos = new List<(string name, CType type)>();
        if (paramsItem is null) { return infos; }

        var ps = Flatten(paramsItem);
        for (var i = 0; i < ps.Count; i++)
        {
            switch (ps[i].Content)
            {
                case Zig.ParamVariadic:
                    if (i != ps.Count - 1)
                    {
                        throw new IrUnsupportedException("zig `...` must be the final parameter");
                    }
                    variadic = true;
                    break;
                case Zig.Param pm:
                    infos.Add((Tok(pm.Arg0), LowerType(pm.Arg2)));
                    break;
                default:
                    throw new IrUnsupportedException("zig param: " + (ps[i].Content?.GetType().Name ?? "null"));
            }
        }
        return infos;
    }

    /// <summary>Declare an <c>extern fn</c> prototype: a function symbol with no body
    /// (so no <see cref="FuncDef"/>). <c>FromSystemHeader = true</c> marks it as
    /// externally provided (libc, linked with <c>-lc</c>) — exactly the marker the C
    /// frontend puts on a libc prototype — so <see cref="LowerCall"/> renders the call
    /// by its bare name (no <c>CalleeSym</c>), routing it to dotcc's <c>Libc</c> runtime
    /// the same way a C program's libc call does. A trailing <c>...</c> (the
    /// <c>fn(fixed, ...)</c> form, e.g. printf) sets the function type's
    /// <c>Variadic</c> flag: the fixed params still coerce at the call, while the
    /// trailing args take C's default argument promotions.</summary>
    private void DeclareExternFn(Item nameTok, Item? paramsItem, Item retType)
    {
        var ret = LowerType(retType);
        var paramInfos = CollectParamInfos(paramsItem, out var variadic);
        _symbols.Declare(new Symbol
        {
            Name = Tok(nameTok),
            Kind = SymKind.Func,
            Type = new CType.Func(ret, paramInfos.Select(p => p.type).ToList(), variadic),
            IsGlobal = true,
            FromSystemHeader = true,
        });
    }

    /// <summary>Pass 2: lower a function body. Params share the function's top scope
    /// (a top-block redecl of a param name is an error in C; Zig likewise), so they
    /// are declared inside the function scope before the body.</summary>
    private void LowerFnBody(Symbol funcSym, List<(string name, CType type)> paramInfos, Item body)
    {
        _currentFnRet = (funcSym.Type as CType.Func)?.Return;
        _symbols.BeginFunction();
        _symbols.EnterScope();
        var paramSyms = paramInfos
            .Select(p => _symbols.Declare(new Symbol { Name = p.name, Kind = SymKind.Param, Type = p.type }))
            .ToList();
        var blk = LowerBlock(body);
        _symbols.ExitScope();

        _ir.Functions.Add(new FuncDef(funcSym, paramSyms, blk, false));
    }

    // ---- statements ------------------------------------------------------

    private Block LowerBlock(Item block)
    {
        var stmts = new List<CStmt>();
        switch (block.Content)
        {
            case Zig.BlockEmpty: break;
            case Zig.Block b:
                foreach (var s in Flatten(b.Arg1)) { stmts.Add(LowerStmt(s)); }
                break;
            default: throw new IrUnsupportedException("zig block: " + (block.Content?.GetType().Name ?? "null"));
        }
        return new Block(stmts);
    }

    private CStmt LowerStmt(Item stmt)
    {
        switch (stmt.Content)
        {
            case Zig.ConstDecl d:       return DeclOf(d.Arg1, null, d.Arg3);
            case Zig.ConstDeclTyped d:  return DeclOf(d.Arg1, d.Arg3, d.Arg5);
            case Zig.VarDecl d:         return DeclOf(d.Arg1, null, d.Arg3);
            case Zig.VarDeclTyped d:    return DeclOf(d.Arg1, d.Arg3, d.Arg5);
            case Zig.StmtReturn r:      return LowerReturn(r.Arg1);
            case Zig.StmtReturnVoid:    return LowerReturnVoid();
            case Zig.StmtExpr e:        return new ExprStmt(LowerExpr(e.Arg0));

            // `x = value;`  → an assignment used as a statement. `_ = value;` is Zig's
            // explicit DISCARD (it forbids ignoring a non-void result) — lower it to a
            // bare expression statement, evaluated for its side effects.
            case Zig.StmtAssign a:
            {
                if (a.Arg0.Content is Zig.Ident lhs && Tok(lhs.Arg0) == "_")
                {
                    return new ExprStmt(LowerExpr(a.Arg2));
                }
                var target = LowerExpr(a.Arg0);
                var value = LowerExpr(a.Arg2);
                return new ExprStmt(new Assign(null, target, value) { Type = target.Type });
            }

            // if (cond) then [else else]  — `then`/`else`/`body` are themselves Stmts
            // (a single statement or a brace Block), which LowerStmt handles uniformly.
            case Zig.StmtIf f:          return new If(LowerExpr(f.Arg2), LowerStmt(f.Arg4), null);
            case Zig.StmtIfElse f:      return new If(LowerExpr(f.Arg2), LowerStmt(f.Arg4), LowerStmt(f.Arg6));
            case Zig.StmtWhile w:       return new While(LowerExpr(w.Arg2), LowerStmt(w.Arg4));

            // `while (cond) : (cont) body` → the C IR `For` (no init): the cont runs after each
            // iteration AND on `continue`, exactly matching C's for-update — so `continue`
            // inside the loop runs the cont, faithful to Zig. The assignment cont (`i = i + 1`)
            // builds an Assign CExpr post (mirroring StmtAssign); the bare-expr cont a plain one.
            case Zig.StmtWhileCont w:
                return new For(null, LowerExpr(w.Arg2), LowerExpr(w.Arg6), LowerStmt(w.Arg8));
            case Zig.StmtWhileContAssign w:
            {
                var post = LowerExpr(w.Arg6);
                var postVal = LowerExpr(w.Arg8);
                var postAssign = new Assign(null, post, postVal) { Type = post.Type };
                return new For(null, LowerExpr(w.Arg2), postAssign, LowerStmt(w.Arg10));
            }

            // `break;` / `continue;` — reuse the C IR loop-control nodes (the C# backend
            // renders them verbatim; valid inside the while/for forms above).
            case Zig.StmtBreak:    return new Break();
            case Zig.StmtContinue: return new Continue();

            // A brace block in statement position (`Stmt -> Block`, pass-through).
            case Zig.Block:
            case Zig.BlockEmpty:        return LowerBlock(stmt);

            default: throw new IrUnsupportedException("zig statement: " + (stmt.Content?.GetType().Name ?? "null"));
        }
    }

    private DeclStmt DeclOf(Item nameTok, Item? typeItem, Item initExpr)
    {
        var init = LowerExpr(initExpr);
        var type = typeItem is not null ? LowerType(typeItem) : (init.Type ?? CType.Int);
        var sym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = type });
        return new DeclStmt(new List<LocalDecl> { new(sym, init) });
    }

    /// <summary>Lower <c>return e;</c>. In a <c>!T</c> function the value becomes an error
    /// union: <c>return error.Foo;</c> → an <see cref="ErrUnionErr"/>; a value that is ALREADY
    /// an error union (<c>return f();</c> where <c>f</c> returns <c>!U</c>) is returned as-is
    /// (Zig doesn't auto-unwrap); any plain value is wrapped in an <see cref="ErrUnionOk"/>.
    /// Outside an error-union function it is a plain <see cref="Return"/>.</summary>
    private CStmt LowerReturn(Item valueItem)
    {
        if (_currentFnRet is CType.ErrorUnion eu)
        {
            if (IsErrorLit(valueItem, out var errName))
            {
                return new Return(new ErrUnionErr(ErrorCode(errName)) { Type = eu });
            }
            var v = LowerExpr(valueItem);
            if (v.Type.Unqualified is CType.ErrorUnion) { return new Return(v); }
            return new Return(new ErrUnionOk(v) { Type = eu });
        }
        return new Return(LowerExpr(valueItem));
    }

    /// <summary>Lower <c>return;</c>. In a <c>!void</c> function it is a success error union
    /// with no payload (<c>ErrUnion&lt;Unit&gt;.Ok(default)</c>); otherwise a plain
    /// <c>return;</c>.</summary>
    private CStmt LowerReturnVoid() =>
        _currentFnRet is CType.ErrorUnion eu
            ? new Return(new ErrUnionOk(null) { Type = eu })
            : new Return(null);

    /// <summary>True when an item is an <c>error.Foo</c> literal, yielding the error name.</summary>
    private static bool IsErrorLit(Item it, out string name)
    {
        if (it.Content is Zig.ErrorLit e) { name = Tok(e.Arg2); return true; }
        name = "";
        return false;
    }

    // ---- expressions -----------------------------------------------------

    private CExpr LowerExpr(Item expr)
    {
        switch (expr.Content)
        {
            case Zig.IntLit i:
            {
                var text = Tok(i.Arg0);
                var value = long.Parse(text, CultureInfo.InvariantCulture);
                return new LitInt(text, value) { Type = CType.Int };
            }
            case Zig.FloatLit f: return new LitFloat(Tok(f.Arg0)) { Type = CType.Double };

            // A string literal. Zig's escape set overlaps C's for the common cases
            // (`\n`/`\t`/`\\`/`\"`/`\xNN`), so we reuse the C string machinery: the raw
            // quoted lexeme becomes a single LitStr segment, typed `char[N]` (decoded
            // byte count incl. NUL) so it decays to `char*` exactly like a C literal —
            // and the C# backend lowers it to the same pooled `Libc.L("…"u8)` pointer.
            // (Zig's `\u{…}` and multiline `\\` strings are deferred.)
            case Zig.StrLit s:
            {
                var segs = new List<string> { Tok(s.Arg0) };   // raw lexeme, quotes included
                DotCC.EmitHelpers.EncodeStringLiteral(segs, out var byteLen);
                return new LitStr(segs) { Type = new CType.Array(CType.Char, byteLen) };
            }
            case Zig.Ident id:
            {
                var name = Tok(id.Arg0);
                var sym = _symbols.Resolve(name)
                    ?? throw new IrUnsupportedException($"unresolved identifier '{name}'");
                return new VarRef(sym) { Type = sym.Type, IsLValue = sym.Kind is SymKind.Var or SymKind.Param };
            }
            case Zig.Grouped g: { var inner = LowerExpr(g.Arg1); return new Paren(inner) { Type = inner.Type }; }

            // if (cond) a else b  — the if-EXPRESSION, lowered to a ternary. Both
            // branches are RhsExpr; the backend wraps the condition in Cond.B.
            case Zig.IfExpr e:
            {
                var then = LowerExpr(e.Arg4);
                return new CondExpr(LowerExpr(e.Arg2), then, LowerExpr(e.Arg6)) { Type = then.Type };
            }

            // arithmetic
            case Zig.Add a:     return Bin(BinOp.Add, a.Arg0, a.Arg2);
            case Zig.Sub a:     return Bin(BinOp.Sub, a.Arg0, a.Arg2);
            case Zig.Mul a:     return Bin(BinOp.Mul, a.Arg0, a.Arg2);
            case Zig.DivOp a:   return Bin(BinOp.Div, a.Arg0, a.Arg2);
            case Zig.ModOp a:   return Bin(BinOp.Mod, a.Arg0, a.Arg2);
            // comparison (non-associative in the grammar)
            case Zig.CmpEq a:   return Bin(BinOp.Eq, a.Arg0, a.Arg2);
            case Zig.CmpNe a:   return Bin(BinOp.Ne, a.Arg0, a.Arg2);
            case Zig.CmpLt a:   return Bin(BinOp.Lt, a.Arg0, a.Arg2);
            case Zig.CmpGt a:   return Bin(BinOp.Gt, a.Arg0, a.Arg2);
            case Zig.CmpLe a:   return Bin(BinOp.Le, a.Arg0, a.Arg2);
            case Zig.CmpGe a:   return Bin(BinOp.Ge, a.Arg0, a.Arg2);
            // boolean (short-circuit)
            case Zig.BoolOr a:  return Bin(BinOp.LogOr, a.Arg0, a.Arg2);
            case Zig.BoolAnd a: return Bin(BinOp.LogAnd, a.Arg0, a.Arg2);
            // bitwise / shift
            case Zig.BitAnd a:  return Bin(BinOp.BitAnd, a.Arg0, a.Arg2);
            case Zig.BitXor a:  return Bin(BinOp.BitXor, a.Arg0, a.Arg2);
            case Zig.BitOr a:   return Bin(BinOp.BitOr, a.Arg0, a.Arg2);
            case Zig.Shl a:     return Bin(BinOp.Shl, a.Arg0, a.Arg2);
            case Zig.Shr a:     return Bin(BinOp.Shr, a.Arg0, a.Arg2);
            // value prefix
            case Zig.PreNeg p:    return Pre(UnOp.Neg, p.Arg1);
            case Zig.PreBitNot p: return Pre(UnOp.BitNot, p.Arg1);
            case Zig.PreNot p:    return Pre(UnOp.LogNot, p.Arg1);
            // Address-of `&x` → a `*T` pointer. Mark a var/param operand AddressTaken so
            // the backend emits a moveable-variable pointer (mirrors IrBuilder.Un's
            // single-site rule). `try` still needs error unions (Milestone B).
            case Zig.PreAddrOf p:
            {
                var operand = LowerExpr(p.Arg1);
                if (Unparen(operand) is VarRef { Sym: { Kind: SymKind.Var or SymKind.Param } s })
                {
                    s.AddressTaken = true;
                }
                return new Unary(UnOp.AddrOf, operand) { Type = new CType.Pointer(operand.Type) };
            }
            // `try e` — unwrap the error union's payload, or propagate its error by throwing
            // ZigErrorReturn (caught at the enclosing `!T` function's emitted try/catch — the
            // backend's Func wrap). An expression, so it works in any position.
            case Zig.PreTry p:
            {
                var inner = LowerExpr(p.Arg1);
                if (inner.Type.Unqualified is not CType.ErrorUnion eu)
                {
                    throw new IrUnsupportedException("zig `try` requires an error-union operand");
                }
                return new ZigTry(inner) { Type = eu.Payload };
            }

            // Postfix deref `p.*` and subscript `a[i]` → the C Unary(Deref)/Index IR.
            // Field access `.field` (Zig.Field) stays deferred — it needs struct field
            // types (Milestone E).
            case Zig.Deref d:
            {
                var operand = LowerExpr(d.Arg0);
                var pointee = operand.Type.Unqualified switch
                {
                    CType.Pointer p => p.Pointee,
                    CType.Array a => a.Element,
                    _ => operand.Type,
                };
                return new Unary(UnOp.Deref, operand) { Type = pointee, IsLValue = true };
            }
            case Zig.Index ix:
            {
                var baseExpr = LowerExpr(ix.Arg0);
                var idx = LowerExpr(ix.Arg2);
                var elem = baseExpr.Type switch
                {
                    CType.Pointer p => p.Pointee,
                    CType.Array a => a.Element,
                    _ => CType.Int,
                };
                return new DotCC.Ir.Index(baseExpr, idx) { Type = elem, IsLValue = true };
            }

            // `.?` optional unwrap. A value optional (CType.Optional → C# `T?`) unwraps via
            // `.Value` (panics on none, matching Zig's `.?`-on-null). An optional POINTER is
            // a bare `T*`, so unwrapping is the identity (the non-null pointer is the same
            // value). [V1: the pointer form does not runtime-check for null.]
            case Zig.Unwrap u:
            {
                var operand = LowerExpr(u.Arg0);
                if (operand.Type.Unqualified is CType.Optional opt)
                {
                    return new Member(operand, "Value", false) { Type = opt.Inner };
                }
                return operand;
            }

            // `@as(T, expr)` — the explicit-type cast builtin → the C Cast IR (RenderCast).
            // Zig 0.16's `@intCast`/`@ptrCast` are result-location-typed (single arg, no
            // explicit target type), which needs the context-type inference dotcc lacks —
            // deferred. arg0 is a type spelled as an expression (handled by LowerType).
            case Zig.BuiltinCall b:
            {
                var bname = Tok(b.Arg0);
                if (bname != "@as")
                {
                    throw new IrUnsupportedException($"zig builtin '{bname}' not lowered yet (only @as is supported)");
                }
                var bargs = Flatten(b.Arg2);
                if (bargs.Count != 2)
                {
                    throw new IrUnsupportedException($"zig `@as` expects (type, value); got {bargs.Count} argument(s)");
                }
                var target = LowerType(bargs[0]);
                return new Cast(target, LowerExpr(bargs[1])) { Type = target };
            }

            // `null` — reuse the C null-pointer node (renders C# `null`, valid for BOTH a
            // pointer sink `T*` and a value-optional sink `T?`). In Zig `null` only appears
            // at a typed sink, so the backend's store-coercion gives it the right form.
            case Zig.NullLit: return new NullPtr { Type = new CType.Pointer(CType.Void) };

            // `a orelse b`. A value optional → C#'s `??` (single-eval LHS, lazy RHS) via
            // NullCoalesce. An optional POINTER → `a != null ? a : b` (C# `??` doesn't apply
            // to pointers); the LHS is named twice there, so a non-trivial (side-effecting)
            // left operand is rejected rather than silently double-evaluated. `orelse return`
            // (a noreturn RHS) isn't expressible in the grammar yet — that's Milestone B2.
            case Zig.OrElse o:
            {
                var left = LowerExpr(o.Arg0);
                var right = LowerExpr(o.Arg2);
                if (left.Type.Unqualified is CType.Optional opt)
                {
                    return new NullCoalesce(left, right) { Type = opt.Inner };
                }
                if (left.Type.Unqualified is CType.Pointer)
                {
                    if (!IsSimpleReeval(left))
                    {
                        throw new IrUnsupportedException(
                            "zig `orelse` on a pointer with a non-trivial left operand not lowered yet (it would be double-evaluated)");
                    }
                    var notNull = new Binary(BinOp.Ne, left, new NullPtr { Type = new CType.Pointer(CType.Void) }) { Type = CType.Int };
                    return new CondExpr(notNull, left, right) { Type = left.Type };
                }
                throw new IrUnsupportedException("zig `orelse` requires an optional left operand");
            }

            // `a catch b` — the error union's payload on success, else the fallback `b` (no
            // propagation). Lowers to the eager `ErrUnion.Catch(a, b)`; since C# evaluates `b`
            // before the call, the fallback must be side-effect-free for that to match Zig's
            // lazy form, so a non-trivial fallback is rejected (deferred) — mirrors the pointer
            // `orelse` rule in B1. `catch |e| …` capture and `catch return` are deferred too.
            case Zig.CatchOp c:
            {
                var union = LowerExpr(c.Arg0);
                if (union.Type.Unqualified is not CType.ErrorUnion eu)
                {
                    throw new IrUnsupportedException("zig `catch` requires an error-union left operand");
                }
                var fallback = LowerExpr(c.Arg2);
                if (!IsSimpleReeval(fallback))
                {
                    throw new IrUnsupportedException(
                        "zig `catch` with a side-effecting fallback not lowered yet (only a literal / variable fallback; `catch |e| …` capture and `catch return` are deferred)");
                }
                return new ZigCatch(union, fallback) { Type = eu.Payload };
            }

            // A bare `error.Foo` value (type `anyerror`) — only `return error.Foo;` is lowered
            // (handled in LowerReturn); a bare error value elsewhere needs error-set modelling.
            case Zig.ErrorLit:
                throw new IrUnsupportedException(
                    "zig bare `error.X` value not lowered yet (only `return error.X;` in a `!T` function)");

            // call of a named function (bare-identifier callee).
            case Zig.CallArgs c:   return LowerCall(c.Arg0, c.Arg2);
            case Zig.CallNoArgs c: return LowerCall(c.Arg0, null);

            default: throw new IrUnsupportedException("zig expression: " + (expr.Content?.GetType().Name ?? "null"));
        }
    }

    /// <summary>Lower a call. V1: only a bare-identifier callee bound to a named
    /// function → an IR <see cref="Call"/> carrying the callee's parameter types (so
    /// the backend coerces each argument as C does at a call) and the resolved symbol
    /// (so it emits the legalized target name). A computed / member callee
    /// (<c>c.printf</c>, a function pointer) is deferred — member calls come with
    /// <c>@cImport</c>.</summary>
    private CExpr LowerCall(Item calleeItem, Item? argListItem)
    {
        if (calleeItem.Content is not Zig.Ident id)
        {
            throw new IrUnsupportedException("zig call: only a bare-identifier callee is lowered yet (got "
                + (calleeItem.Content?.GetType().Name ?? "null") + ")");
        }
        var name = Tok(id.Arg0);
        var sym = _symbols.Resolve(name)
            ?? throw new IrUnsupportedException($"call to unresolved name '{name}'");
        if (sym.Type.Unqualified is not CType.Func fn)
        {
            throw new IrUnsupportedException($"'{name}' is not a function (indirect / fn-ptr calls deferred)");
        }

        var argItems = argListItem is null ? new List<Item>() : Flatten(argListItem);
        var args = new List<CExpr>(argItems.Count);
        foreach (var a in argItems) { args.Add(LowerExpr(a)); }
        // A variadic callee (printf) needs AT LEAST the fixed params; the rest are the
        // variadic tail. A fixed-arity callee needs an exact match.
        var arityOk = fn.Variadic ? args.Count >= fn.Params.Count : args.Count == fn.Params.Count;
        if (!arityOk)
        {
            throw new IrUnsupportedException(
                $"call to '{name}': expected {(fn.Variadic ? "at least " : "")}{fn.Params.Count} argument(s), got {args.Count}");
        }
        // Zig parity (the differential oracle caught dotcc being too lenient here): an
        // untyped comptime numeric literal has no fixed-size ABI type, so Zig forbids
        // passing it to a C-variadic — `printf("%d", 42)` is an error, `@as(c_int, 42)`
        // (or any concretely-typed value) is required. Reject the variadic tail the same
        // way, with zig's exact wording, via the same diagnostics channel C uses for
        // constraint violations (write-to-const).
        if (fn.Variadic)
        {
            for (var k = fn.Params.Count; k < argItems.Count; k++)
            {
                if (IsComptimeUntypedNumeric(argItems[k]))
                {
                    _ir.Diagnostics.Add(new Diagnostic(Severity.Error,
                        "integer and float literals passed to variadic function must be casted to a fixed-size number type",
                        SrcPos.From(argItems[k])));
                }
            }
        }

        // An extern/libc function (FromSystemHeader) renders by its bare name — no
        // CalleeSym — so it binds to dotcc's Libc runtime (and printf/scanf hit the
        // fluent builder), exactly as a C program's libc call does. A user Zig
        // function carries its symbol so the (possibly legalized) target name is used.
        var calleeSym = sym.FromSystemHeader ? null : sym;
        return new Call(name, args, fn.Params, calleeSym) { Type = fn.Return };
    }

    /// <summary>Lower a binary op, synthesizing the result type the way the C# backend
    /// will treat it: usual-arithmetic for arithmetic/bitwise, the promoted left type
    /// for a shift (operands promote independently), and <c>int</c> for a relational /
    /// boolean (the backend renders those as an integer-valued <c>(CBool)(…)</c>).</summary>
    private CExpr Bin(BinOp op, Item l, Item r)
    {
        var left = LowerExpr(l);
        var right = LowerExpr(r);
        var type = op switch
        {
            BinOp.Eq or BinOp.Ne or BinOp.Lt or BinOp.Gt or BinOp.Le or BinOp.Ge
                or BinOp.LogAnd or BinOp.LogOr => CType.Int,
            BinOp.Shl or BinOp.Shr => CType.IntegerPromote(left.Type),
            _ => CType.UsualArithmetic(left.Type, right.Type),
        };
        return new Binary(op, left, right) { Type = type };
    }

    /// <summary>Lower a value-prefix unary op. <c>!x</c> yields an int (the backend
    /// renders it 0/1); <c>-x</c>/<c>~x</c> take the integer-promoted operand type.</summary>
    private CExpr Pre(UnOp op, Item operandItem)
    {
        var operand = LowerExpr(operandItem);
        var type = op == UnOp.LogNot ? CType.Int : CType.IntegerPromote(operand.Type);
        return new Unary(op, operand) { Type = type };
    }

    /// <summary>Peel redundant <see cref="Paren"/> wrappers to reach the inner expr
    /// (so `&(x)` still marks `x` AddressTaken). Mirrors <c>IrBuilder.Unparen</c>.</summary>
    private static CExpr Unparen(CExpr e) => e is Paren p ? Unparen(p.Inner) : e;

    /// <summary>True for an expression with no side effects, safe to render more than once
    /// — the pointer <c>orelse</c> lowers to <c>a != null ? a : b</c>, naming <c>a</c>
    /// twice. Conservative: a var/param read, a literal, <c>null</c>, or a parenthesized
    /// such; anything else (a call, an assignment) is rejected to avoid double evaluation.</summary>
    private static bool IsSimpleReeval(CExpr e) => e switch
    {
        VarRef or NullPtr or LitInt or LitFloat => true,
        Paren p => IsSimpleReeval(p.Inner),
        _ => false,
    };

    /// <summary>True if the expression is a comptime-only numeric value — an int/float
    /// literal, or arithmetic over such (Zig's <c>comptime_int</c>/<c>comptime_float</c>).
    /// These have no fixed-size ABI type, so Zig forbids passing them to a C-variadic.
    /// The moment a concrete-typed leaf appears (identifier, call, <c>@as</c>, deref,
    /// index) the expression is typed and allowed across the variadic boundary.</summary>
    private static bool IsComptimeUntypedNumeric(Item it) => it.Content switch
    {
        Zig.IntLit or Zig.FloatLit => true,
        Zig.Grouped g   => IsComptimeUntypedNumeric(g.Arg1),
        Zig.PreNeg p    => IsComptimeUntypedNumeric(p.Arg1),
        Zig.PreBitNot p => IsComptimeUntypedNumeric(p.Arg1),
        Zig.Add a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.Sub a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.Mul a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.DivOp a  => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.ModOp a  => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.BitAnd a => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.BitXor a => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.BitOr a  => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.Shl a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.Shr a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        _ => false,
    };

    // ---- types -----------------------------------------------------------

    private CType LowerType(Item type) => type.Content switch
    {
        Zig.Ident id => LowerPrim(Tok(id.Arg0)),
        // Pointer types. `*T` and the C-pointer `[*c]T` both lower to a plain
        // `T*` (the C-pointer's null/arithmetic semantics ARE C's pointer). The
        // pointee `const` rides as a TypeQual so const-correctness sees it; it
        // doesn't change the C# spelling (`[*c]const u8` and `[*c]u8` are both
        // `byte*`). `[*c]const u8` is exactly the type of printf's format param.
        Zig.TyPointer p    => new CType.Pointer(LowerType(p.Arg1)),
        Zig.TyPtrConst p   => new CType.Pointer(LowerType(p.Arg2).WithQuals(TypeQual.Const)),
        Zig.TyCPtr p       => new CType.Pointer(LowerType(p.Arg1)),
        Zig.TyCPtrConst p  => new CType.Pointer(LowerType(p.Arg2).WithQuals(TypeQual.Const)),
        // `?T` optional. An optional POINTER `?*T` lowers to a bare nullable `T*` (Zig's
        // own niche — null = none, zero cost; a non-optional `*T` loses its non-null
        // guarantee, a documented leniency). A `?T` over a value type lowers to C#
        // Nullable<T> via CType.Optional, so `null`/`.?`/`orelse` map to C#'s built-ins.
        Zig.TyOptional opt => LowerOptional(opt.Arg1),
        // `E!T` error-union type → CType.ErrorUnion(T). V1 erases the error SET (Arg0, the
        // Suffix naming the set), so `anyerror!T` and a named `E!T` lower identically — the
        // payload is what the backend renders (`ErrUnion<T>`). See [[CType.ErrorUnion]].
        Zig.ErrUnion eu => new CType.ErrorUnion(LowerType(eu.Arg2)),
        _ => throw new IrUnsupportedException("zig type: " + (type.Content?.GetType().Name ?? "null")),
    };

    /// <summary>Lower a Zig optional payload type: a pointer payload stays a bare nullable
    /// pointer (the niche); any other payload is wrapped in <see cref="CType.Optional"/>
    /// (→ C# <c>T?</c>).</summary>
    private CType LowerOptional(Item innerType)
    {
        var inner = LowerType(innerType);
        return inner.Unqualified is CType.Pointer ? inner : new CType.Optional(inner);
    }

    /// <summary>Map a Zig primitive type name to its faithful C# lowering. The
    /// fixed-width integers carry real signedness (i8 → <c>sbyte</c>, u8 → <c>byte</c>,
    /// …), unlike the earlier slice that collapsed both 8-bit forms to <c>byte</c>.
    /// <c>usize</c>/<c>isize</c> map to the LP64 64-bit <c>size_t</c>/<c>long</c>
    /// (width-correct on dotcc's target; a dedicated pointer-width type is a later
    /// refinement). <c>comptime_int</c>/<c>comptime_float</c> and the bigger/arbitrary
    /// <c>iN</c>/<c>uN</c> widths are deferred.</summary>
    private static CType LowerPrim(string name) => name switch
    {
        "void" => CType.Void,
        "bool" => CType.Bool,
        "i8"  => CType.SChar,    // → C# sbyte
        "u8"  => CType.UChar,    // → C# byte
        "i16" => CType.Short,
        "u16" => CType.UShort,
        "i32" => CType.Int,
        "u32" => CType.UInt,
        "i64" => CType.Long,
        "u64" => CType.ULong,
        "isize" => CType.Long,   // LP64: pointer-width signed
        "usize" => CType.ULong,  // LP64: pointer-width unsigned (== size_t)
        "f32" => CType.Float,
        "f64" => CType.Double,
        // C-ABI types for `extern fn` libc FFI (LP64, matching dotcc's __LP64__ trio:
        // `c_long`/`c_ulong` are 8 bytes). These map onto the same well-known prims the
        // C frontend uses, so RenderType + the coercion tables already cover them.
        "c_char" => CType.Char,
        "c_short" => CType.Short,
        "c_ushort" => CType.UShort,
        "c_int" => CType.Int,
        "c_uint" => CType.UInt,
        "c_long" => CType.Long,
        "c_ulong" => CType.ULong,
        "c_longlong" => CType.LongLong,
        "c_ulonglong" => CType.ULongLong,
        _ => throw new IrUnsupportedException($"zig type '{name}' not supported yet (slice)"),
    };

    // ---- helpers ---------------------------------------------------------

    /// <summary>Flatten a left-recursive list spine (Decls / Stmts / Params / ArgList)
    /// into source order with an explicit stack — same anti-stack-overflow walk as
    /// <see cref="IrBuilder"/>'s <c>FlattenFns</c>. The cons/one node types are disjoint
    /// across the four list kinds, and the walk stops at the first non-list node (so a
    /// nested Block's own Stmts aren't pulled into the parent), so one method serves all
    /// four with no cross-contamination.</summary>
    private static List<Item> Flatten(Item it)
    {
        var stack = new Stack<Item>();
        stack.Push(it);
        var ordered = new List<Item>();
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            switch (n.Content)
            {
                case Zig.DeclsCons c:  stack.Push(c.Arg1); stack.Push(c.Arg0); break;  // [Decl, Decls]
                case Zig.DeclsOne o:   stack.Push(o.Arg0); break;
                case Zig.StmtsCons c:  stack.Push(c.Arg1); stack.Push(c.Arg0); break;  // [Stmt, Stmts]
                case Zig.StmtsOne o:   stack.Push(o.Arg0); break;
                case Zig.ParamsCons c: stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [Param, ',', Params]
                case Zig.ParamsOne o:  stack.Push(o.Arg0); break;
                case Zig.ArgsCons c:   stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [Expr, ',', ArgList]
                case Zig.ArgsOne o:    stack.Push(o.Arg0); break;
                default: ordered.Add(n); break;
            }
        }
        return ordered;
    }
}
