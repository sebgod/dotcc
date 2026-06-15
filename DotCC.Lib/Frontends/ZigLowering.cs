#nullable enable

using System.Collections.Generic;
using System.Globalization;
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
/// VERTICAL SLICE: just enough to compile <c>pub fn main() u8 { const x: u8 = 40;
/// return x + 2; }</c> end-to-end (fn def, typed/untyped const, return, identifier,
/// integer literal, +/-/* arithmetic). Everything else throws
/// <see cref="IrUnsupportedException"/> — fail loudly, grow deliberately.
/// </summary>
internal sealed class ZigLowering
{
    private readonly IrBuilder _ir;
    private readonly SymbolTable _symbols;

    public ZigLowering(IrBuilder ir, INameLegalizer names)
    {
        _ir = ir;
        _symbols = new SymbolTable(names);
    }

    private static string Tok(Item it) => it.Content as string
        ?? throw new IrUnsupportedException("expected a token lexeme");

    public void Lower(Item root)
    {
        foreach (var decl in Flatten(root, isDecls: true)) { LowerDecl(decl); }
    }

    // ---- top level -------------------------------------------------------

    private void LowerDecl(Item decl)
    {
        switch (decl.Content)
        {
            case Zig.PubFn p:            LowerDecl(p.Arg1); break;            // unwrap `pub`
            case Zig.FnDef f:            LowerFn(f.Arg1, f.Arg3, f.Arg5, f.Arg6); break;
            case Zig.FnDefNoArgs f:      LowerFn(f.Arg1, null, f.Arg4, f.Arg5); break;
            case Zig.FnDefErr f:         LowerFn(f.Arg1, f.Arg3, f.Arg6, f.Arg7); break;   // `!T` return — error union deferred, use T
            case Zig.FnDefNoArgsErr f:   LowerFn(f.Arg1, null, f.Arg5, f.Arg6); break;
            default: throw new IrUnsupportedException("zig top-level decl: " + (decl.Content?.GetType().Name ?? "null"));
        }
    }

    private void LowerFn(Item nameTok, Item? paramsItem, Item retType, Item body)
    {
        var ret = LowerType(retType);
        var paramTypes = new List<CType>();
        // (slice: param list lowering deferred — main() takes none.)
        if (paramsItem is not null) { throw new IrUnsupportedException("zig fn parameters not lowered yet (slice)"); }

        var funcSym = _symbols.Declare(new Symbol
        {
            Name = Tok(nameTok),
            Kind = SymKind.Func,
            Type = new CType.Func(ret, paramTypes, false),
            IsGlobal = true,
        });

        _symbols.BeginFunction();
        _symbols.EnterScope();
        var paramSyms = new List<Symbol>();
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
                foreach (var s in Flatten(b.Arg1, isDecls: false)) { stmts.Add(LowerStmt(s)); }
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
            case Zig.StmtReturn r:      return new Return(LowerExpr(r.Arg1));
            case Zig.StmtReturnVoid:    return new Return(null);
            case Zig.StmtExpr e:        return new ExprStmt(LowerExpr(e.Arg0));
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
            case Zig.Ident id:
            {
                var name = Tok(id.Arg0);
                var sym = _symbols.Resolve(name)
                    ?? throw new IrUnsupportedException($"unresolved identifier '{name}'");
                return new VarRef(sym) { Type = sym.Type, IsLValue = sym.Kind is SymKind.Var or SymKind.Param };
            }
            case Zig.Grouped g: { var inner = LowerExpr(g.Arg1); return new Paren(inner) { Type = inner.Type }; }
            case Zig.Add a:     return Bin(BinOp.Add, a.Arg0, a.Arg2);
            case Zig.Sub a:     return Bin(BinOp.Sub, a.Arg0, a.Arg2);
            case Zig.Mul a:     return Bin(BinOp.Mul, a.Arg0, a.Arg2);
            case Zig.DivOp a:   return Bin(BinOp.Div, a.Arg0, a.Arg2);
            case Zig.ModOp a:   return Bin(BinOp.Mod, a.Arg0, a.Arg2);
            default: throw new IrUnsupportedException("zig expression: " + (expr.Content?.GetType().Name ?? "null"));
        }
    }

    private CExpr Bin(BinOp op, Item l, Item r) =>
        new Binary(op, LowerExpr(l), LowerExpr(r)) { Type = CType.Int };

    // ---- types -----------------------------------------------------------

    private CType LowerType(Item type) => type.Content switch
    {
        Zig.Ident id => LowerPrim(Tok(id.Arg0)),
        _ => throw new IrUnsupportedException("zig type: " + (type.Content?.GetType().Name ?? "null")),
    };

    // Slice subset; 8-bit signedness is deferred (both map to C# byte for now).
    private static CType LowerPrim(string name) => name switch
    {
        "i8" or "u8" => CType.Char,   // → C# byte
        "i32" => CType.Int,
        "u32" => CType.UInt,
        "i64" => CType.Long,
        _ => throw new IrUnsupportedException($"zig type '{name}' not supported yet (slice)"),
    };

    // ---- helpers ---------------------------------------------------------

    /// <summary>Flatten a left-/right-recursive list (Decls or Stmts) into source
    /// order with an explicit stack — same anti-stack-overflow walk as
    /// <see cref="IrBuilder"/>'s <c>FlattenFns</c>.</summary>
    private static List<Item> Flatten(Item it, bool isDecls)
    {
        var stack = new Stack<Item>();
        stack.Push(it);
        var ordered = new List<Item>();
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            switch (n.Content)
            {
                case Zig.DeclsCons c when isDecls: stack.Push(c.Arg1); stack.Push(c.Arg0); break;
                case Zig.DeclsOne o when isDecls:  stack.Push(o.Arg0); break;
                case Zig.StmtsCons c when !isDecls: stack.Push(c.Arg1); stack.Push(c.Arg0); break;
                case Zig.StmtsOne o when !isDecls:  stack.Push(o.Arg0); break;
                default: ordered.Add(n); break;
            }
        }
        return ordered;
    }
}
