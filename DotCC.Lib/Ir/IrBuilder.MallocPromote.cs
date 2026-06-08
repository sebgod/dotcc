#nullable enable

using System.Collections.Generic;
using System.Linq;

namespace DotCC.Ir;

internal sealed partial class IrBuilder
{
    // ---- malloc/free → stack-value peephole --------------------------------
    //
    // A local `T* p = (T*)malloc(sizeof(T))` (or the cast-less form) that is used
    // ONLY via `p->field` and `free(p)` — never escaping (not returned, assigned,
    // address-taken, subscripted, passed anywhere but free, or pointer-arithmetic'd)
    // — is demoted to a stack value `T p = new T()`: each `->` becomes `.` and the
    // `free` is dropped. This is a STRUCTURAL recognition over the typed IR (node
    // shapes + a sound escape check), never a text match. The analysis is
    // conservative: any construct it doesn't explicitly understand disqualifies the
    // candidate (a missed promotion is harmless; a wrong one would dangle).

    /// <summary>Run the malloc→stack peephole over a function body, returning the
    /// rewritten block (or the original when nothing is promotable).</summary>
    private Block PromoteMallocs(Block body)
    {
        var candidates = new Dictionary<Symbol, CType>();   // sym → pointee struct type
        CollectMallocDecls(body, candidates);
        if (candidates.Count == 0) { return body; }

        var promote = new HashSet<Symbol>();
        foreach (var (sym, _) in candidates)
        {
            if (UsesAreSafe(body, sym) && CountFrees(body, sym) >= 1) { promote.Add(sym); }
        }
        if (promote.Count == 0) { return body; }

        // Retype each promoted symbol to the value type IN PLACE so every shared
        // VarRef reports the struct value type (drives the demoted declaration).
        foreach (var sym in promote) { sym.Type = candidates[sym]; }
        return (Block)RewriteMallocStmt(body, promote)!;
    }

    /// <summary>Does <paramref name="init"/> match <c>[(T*)] malloc(sizeof(T))</c>
    /// for the declared pointer's struct pointee? Returns the pointee type.</summary>
    private static bool IsMallocOfStruct(Symbol sym, CExpr? init, out CType pointee)
    {
        pointee = CType.Void;
        if (sym.Type.Unqualified is not CType.Pointer { Pointee: var pt } || pt.Unqualified is not CType.Named) { return false; }
        if (init is null) { return false; }
        var call = Unparen(init);
        if (call is Cast c) { call = Unparen(c.Operand); }
        if (call is not Call { Callee: "malloc", Args: { Count: 1 } args }) { return false; }
        var arg = Unparen(args[0]);
        if (arg is Cast ac) { arg = Unparen(ac.Operand); }
        if (arg is not SizeOfExpr so || so.Of.Unqualified.CsType != pt.Unqualified.CsType) { return false; }
        pointee = pt;
        return true;
    }

    private void CollectMallocDecls(CStmt s, Dictionary<Symbol, CType> into)
    {
        switch (s)
        {
            case Block b: foreach (var st in b.Stmts) { CollectMallocDecls(st, into); } break;
            case DeclStmt d:
                foreach (var ld in d.Decls)
                {
                    if (IsMallocOfStruct(ld.Sym, ld.Init, out var pointee)) { into[ld.Sym] = pointee; }
                }
                break;
            case If f: CollectMallocDecls(f.Then, into); if (f.Else is { } e) { CollectMallocDecls(e, into); } break;
            case While w: CollectMallocDecls(w.Body, into); break;
            case DoWhile dw: CollectMallocDecls(dw.Body, into); break;
            case For fr: if (fr.Init is { } fi) { CollectMallocDecls(fi, into); } CollectMallocDecls(fr.Body, into); break;
            case Labeled lb: CollectMallocDecls(lb.Body, into); break;
            case CaseLabelStmt cl: CollectMallocDecls(cl.Body, into); break;
            case Switch sw: foreach (var sec in sw.Sections) { foreach (var st in sec.Body) { CollectMallocDecls(st, into); } } break;
            case SetjmpGuard sj:
                if (sj.TryBody is { } tb) { CollectMallocDecls(tb, into); }
                if (sj.CatchBody is { } cb) { CollectMallocDecls(cb, into); }
                break;
        }
    }

    // ---- escape analysis: every use of `sym` must be p->field or free(p) -----

    /// <summary>An expression is SAFE for promotion when every <see cref="VarRef"/>
    /// of <paramref name="sym"/> within it appears only as the direct base of a
    /// <c>p-&gt;field</c> access or the sole argument of <c>free(p)</c>. Any other
    /// occurrence — or any node shape this doesn't model — is unsafe (conservative,
    /// so a promotion never dangles).</summary>
    private static bool SafeExpr(CExpr? e, Symbol sym) => e switch
    {
        null => true,
        LitInt or LitFloat or LitStr or EnumConstRef or Raw or SizeOfExpr or OffsetOf or DefaultLit or StackNew => true,
        VarRef v => v.Sym != sym,   // a bare reference to the candidate is an escape
        // p->field: the base is the candidate itself — allowed, and consumed here.
        Member { Arrow: true } m when Unparen(m.Base) is VarRef bv && bv.Sym == sym => true,
        Member m => SafeExpr(m.Base, sym),
        // free(p): the candidate as free's sole arg — allowed, consumed here.
        Call { Callee: "free", Args: { Count: 1 } fa } when Unparen(fa[0]) is VarRef fv && fv.Sym == sym => true,
        Call c => c.Args.All(a => SafeExpr(a, sym)),
        IndirectCall ic => SafeExpr(ic.Callee, sym) && ic.Args.All(a => SafeExpr(a, sym)),
        Unary u => SafeExpr(u.Operand, sym),
        Binary b => SafeExpr(b.Left, sym) && SafeExpr(b.Right, sym),
        Assign a => SafeExpr(a.Target, sym) && SafeExpr(a.Value, sym),
        Cast c => SafeExpr(c.Operand, sym),
        CondExpr t => SafeExpr(t.Cond, sym) && SafeExpr(t.Then, sym) && SafeExpr(t.Else, sym),
        Index ix => SafeExpr(ix.Base, sym) && SafeExpr(ix.Idx, sym),
        CommaSeq cs => cs.Items.All(i => SafeExpr(i, sym)),
        CommaOp co => co.Items.All(i => SafeExpr(i, sym)),
        Paren p => SafeExpr(p.Inner, sym),
        VaArgGet va => SafeExpr(va.Ap, sym),
        StructInit si => si.Members.All(m => SafeExpr(m.Value, sym)),
        StackArray sa => sa.Elems.All(x => SafeExpr(x, sym)),
        PinnedArray pa => (pa.Elems?.All(x => SafeExpr(x, sym)) ?? true) && (pa.Count is null || SafeExpr(pa.Count, sym)),
        _ => false,   // an unmodeled node: conservatively unsafe
    };

    private static bool UsesAreSafe(CStmt s, Symbol sym) => s switch
    {
        Block b => b.Stmts.All(x => UsesAreSafe(x, sym)),
        DeclStmt d => d.Decls.All(ld => SafeExpr(ld.Init, sym)),
        // free(p) as a statement is allowed (it will be dropped); other expr-stmts
        // must be safe.
        ExprStmt es => IsFreeOf(es.Expr, sym) || SafeExpr(es.Expr, sym),
        If f => SafeExpr(f.Cond, sym) && UsesAreSafe(f.Then, sym) && (f.Else is null || UsesAreSafe(f.Else, sym)),
        While w => SafeExpr(w.Cond, sym) && UsesAreSafe(w.Body, sym),
        DoWhile dw => UsesAreSafe(dw.Body, sym) && SafeExpr(dw.Cond, sym),
        For fr => (fr.Init is null || UsesAreSafe(fr.Init, sym)) && SafeExpr(fr.Cond, sym)
                  && SafeExpr(fr.Post, sym) && UsesAreSafe(fr.Body, sym),
        Return r => SafeExpr(r.Value, sym),
        Break or Continue or Goto => true,
        Labeled lb => UsesAreSafe(lb.Body, sym),
        CaseLabelStmt cl => SafeExpr(cl.CaseExpr, sym) && UsesAreSafe(cl.Body, sym),
        Switch sw => SafeExpr(sw.Subject, sym) && sw.Sections.All(sec => sec.Body.All(st => UsesAreSafe(st, sym))),
        SetjmpGuard sj => SafeExpr(sj.Env, sym) && (sj.TryBody is null || UsesAreSafe(sj.TryBody, sym))
                          && (sj.CatchBody is null || UsesAreSafe(sj.CatchBody, sym)),
        ArrayDecl ad => SafeExpr(ad.CountExpr, sym) && (ad.Inits?.All(x => SafeExpr(x, sym)) ?? true),
        _ => false,
    };

    private static bool IsFreeOf(CExpr e, Symbol sym) =>
        Unparen(e) is Call { Callee: "free", Args: { Count: 1 } a } && Unparen(a[0]) is VarRef v && v.Sym == sym;

    private static int CountFrees(CStmt s, Symbol sym)
    {
        var n = 0;
        void E(CExpr? e) { if (e is not null && IsFreeOf(e, sym)) { n++; } }
        void S(CStmt st)
        {
            switch (st)
            {
                case Block b: foreach (var x in b.Stmts) { S(x); } break;
                case ExprStmt es: E(es.Expr); break;
                case If f: S(f.Then); if (f.Else is { } e) { S(e); } break;
                case While w: S(w.Body); break;
                case DoWhile dw: S(dw.Body); break;
                case For fr: if (fr.Init is { } fi) { S(fi); } S(fr.Body); break;
                case Labeled lb: S(lb.Body); break;
                case CaseLabelStmt cl: S(cl.Body); break;
                case Switch sw: foreach (var sec in sw.Sections) { foreach (var x in sec.Body) { S(x); } } break;
                case SetjmpGuard sj: if (sj.TryBody is { } tb) { S(tb); } if (sj.CatchBody is { } cb) { S(cb); } break;
            }
        }
        S(s);
        return n;
    }

    // ---- rewrite: demote decl, arrow→dot, drop free ------------------------

    private CExpr RewriteMallocExpr(CExpr e, HashSet<Symbol> promote) => e switch
    {
        // p->field → p.field (the symbol is already retyped to the struct value).
        Member { Arrow: true } m when Unparen(m.Base) is VarRef bv && promote.Contains(bv.Sym)
            => m with { Base = RewriteMallocExpr(m.Base, promote), Arrow = false },
        Member m => m with { Base = RewriteMallocExpr(m.Base, promote) },
        Unary u => u with { Operand = RewriteMallocExpr(u.Operand, promote) },
        Binary b => b with { Left = RewriteMallocExpr(b.Left, promote), Right = RewriteMallocExpr(b.Right, promote) },
        Assign a => a with { Target = RewriteMallocExpr(a.Target, promote), Value = RewriteMallocExpr(a.Value, promote) },
        Call c => c with { Args = c.Args.Select(x => RewriteMallocExpr(x, promote)).ToList() },
        IndirectCall ic => ic with { Callee = RewriteMallocExpr(ic.Callee, promote), Args = ic.Args.Select(x => RewriteMallocExpr(x, promote)).ToList() },
        Cast c => c with { Operand = RewriteMallocExpr(c.Operand, promote) },
        CondExpr t => t with { Cond = RewriteMallocExpr(t.Cond, promote), Then = RewriteMallocExpr(t.Then, promote), Else = RewriteMallocExpr(t.Else, promote) },
        Index ix => ix with { Base = RewriteMallocExpr(ix.Base, promote), Idx = RewriteMallocExpr(ix.Idx, promote) },
        CommaSeq cs => cs with { Items = cs.Items.Select(x => RewriteMallocExpr(x, promote)).ToList() },
        CommaOp co => co with { Items = co.Items.Select(x => RewriteMallocExpr(x, promote)).ToList() },
        Paren p => p with { Inner = RewriteMallocExpr(p.Inner, promote) },
        VaArgGet va => va with { Ap = RewriteMallocExpr(va.Ap, promote) },
        StructInit si => si with { Members = si.Members.Select(m => m with { Value = RewriteMallocExpr(m.Value, promote) }).ToList() },
        _ => e,
    };

    private CExpr? RewriteMallocExprN(CExpr? e, HashSet<Symbol> promote) => e is null ? null : RewriteMallocExpr(e, promote);

    /// <summary>Rewrite a statement; returns null when it should be DROPPED (a
    /// promoted <c>free(p)</c>).</summary>
    private CStmt? RewriteMallocStmt(CStmt s, HashSet<Symbol> promote)
    {
        switch (s)
        {
            case Block b:
                return b with { Stmts = b.Stmts.Select(x => RewriteMallocStmt(x, promote)).Where(x => x is not null).Select(x => x!).ToList() };
            case ExprStmt es when promote.Any(sym => IsFreeOf(es.Expr, sym)):
                return null;   // drop the free of a promoted pointer
            case DeclStmt d:
                return d with { Decls = d.Decls.Select(ld =>
                    promote.Contains(ld.Sym)
                        ? ld with { Init = new StackNew(ld.Sym.Type) { Type = ld.Sym.Type, Pos = ld.Init?.Pos ?? default } }
                        : ld with { Init = RewriteMallocExprN(ld.Init, promote) }).ToList() };
            case ExprStmt es: return es with { Expr = RewriteMallocExpr(es.Expr, promote) };
            case If f: return f with { Cond = RewriteMallocExpr(f.Cond, promote), Then = RewriteMallocStmt(f.Then, promote)!, Else = f.Else is { } e ? RewriteMallocStmt(e, promote) : null };
            case While w: return w with { Cond = RewriteMallocExpr(w.Cond, promote), Body = RewriteMallocStmt(w.Body, promote)! };
            case DoWhile dw: return dw with { Body = RewriteMallocStmt(dw.Body, promote)!, Cond = RewriteMallocExpr(dw.Cond, promote) };
            case For fr: return fr with { Init = fr.Init is { } fi ? RewriteMallocStmt(fi, promote) : null, Cond = RewriteMallocExprN(fr.Cond, promote), Post = RewriteMallocExprN(fr.Post, promote), Body = RewriteMallocStmt(fr.Body, promote)! };
            case Return r: return r with { Value = RewriteMallocExprN(r.Value, promote) };
            case Labeled lb: return lb with { Body = RewriteMallocStmt(lb.Body, promote)! };
            case CaseLabelStmt cl: return cl with { CaseExpr = RewriteMallocExprN(cl.CaseExpr, promote), Body = RewriteMallocStmt(cl.Body, promote)! };
            case Switch sw: return sw with { Subject = RewriteMallocExpr(sw.Subject, promote), Sections = sw.Sections.Select(sec => sec with { Body = sec.Body.Select(x => RewriteMallocStmt(x, promote)).Where(x => x is not null).Select(x => x!).ToList() }).ToList() };
            case SetjmpGuard sj: return sj with { Env = RewriteMallocExpr(sj.Env, promote), TryBody = sj.TryBody is { } tb ? RewriteMallocStmt(tb, promote) : null, CatchBody = sj.CatchBody is { } cb ? RewriteMallocStmt(cb, promote) : null };
            case ArrayDecl ad: return ad with { CountExpr = RewriteMallocExprN(ad.CountExpr, promote), Inits = ad.Inits?.Select(x => RewriteMallocExpr(x, promote)).ToList() };
            default: return s;
        }
    }
}
