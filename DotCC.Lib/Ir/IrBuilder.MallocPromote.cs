#nullable enable

using System.Collections.Generic;
using System.Linq;

namespace DotCC.Ir;

internal sealed partial class IrBuilder
{
    // ---- malloc/free → stack peephole --------------------------------------
    //
    // Two shapes of a non-escaping, freed `malloc` are demoted to the stack:
    //
    //   * STRUCT:  `T* p = (T*)malloc(sizeof(T))` used ONLY via `p->field` and
    //     `free(p)` → a stack value `T p = new T()` (each `->` becomes `.`, the
    //     `free` is dropped, the symbol is retyped pointer→value).
    //   * BUFFER (V1): a sole-declarator `char* p = malloc(N)` with a COMPILE-TIME
    //     CONSTANT byte count N (≤ MaxStackPromoteBytes), used ONLY via `p[i]` and
    //     `free(p)`, whose declaration is NOT inside a loop → a `stackalloc`
    //     (`byte* p = stackalloc byte[N]`, an ArrayDecl). The symbol KEEPS its
    //     pointer type, so every subscript stays valid with no expression rewrite;
    //     only the decl is swapped and the `free` dropped.
    //
    // Both are STRUCTURAL recognitions over the typed IR (node shapes + a sound
    // escape check), never a text match. The analysis is conservative: any
    // construct it doesn't explicitly model disqualifies the candidate (a missed
    // promotion is harmless; a wrong one would dangle). The buffer path is the
    // soundest minimal slice — V2 adds a non-capturing-callee allowlist (memcpy /
    // snprintf / …) and null-test tolerance (`if (!p)`), the two patterns that make
    // the idiomatic null-checked-then-libc'd malloc promotable.

    /// <summary>The byte ceiling for buffer promotion: a constant-size `malloc`
    /// above this stays on the heap. `malloc(huge)` returns NULL (recoverable);
    /// `stackalloc huge` overflows the stack (unrecoverable), so the transform only
    /// fires on a bounded, modest size.</summary>
    private const long MaxStackPromoteBytes = 1024;

    /// <summary>Buffer-promotion bookkeeping for one symbol: the stackalloc element
    /// type (the unqualified char-family pointee → C# <c>byte</c>) and the constant
    /// element count.</summary>
    private readonly record struct BufferPromote(CType Element, long Count);

    /// <summary>The three promotion sets threaded through the rewrite: <see cref="Struct"/>
    /// symbols retype pointer→value (drive <c>StackNew</c> + arrow→dot); <see cref="Buffer"/>
    /// symbols keep their pointer type and have their decl swapped to a <c>stackalloc</c>
    /// per <see cref="BufInfo"/>; a <c>free</c> of any symbol in either set is dropped.</summary>
    private readonly record struct PromoteCtx(
        HashSet<Symbol> Struct,
        HashSet<Symbol> Buffer,
        Dictionary<Symbol, BufferPromote> BufInfo)
    {
        /// <summary>True when <paramref name="sym"/> is promoted by either path — its
        /// <c>free</c> must be dropped.</summary>
        public bool IsFreeable(Symbol sym) => Struct.Contains(sym) || Buffer.Contains(sym);
    }

    /// <summary>Run the malloc→stack peephole over a function body, returning the
    /// rewritten block (or the original when nothing is promotable).</summary>
    private Block PromoteMallocs(Block body)
    {
        var structCands = new Dictionary<Symbol, CType>();   // sym → pointee struct type
        CollectMallocDecls(body, structCands);

        var bufferCands = new Dictionary<Symbol, BufferPromote>();
        CollectMallocBuffers(body, bufferCands, inLoop: false);

        if (structCands.Count == 0 && bufferCands.Count == 0) { return body; }

        var promoteStruct = new HashSet<Symbol>();
        foreach (var (sym, _) in structCands)
        {
            if (UsesAreSafe(body, sym) && CountFrees(body, sym) >= 1) { promoteStruct.Add(sym); }
        }

        var promoteBuffer = new HashSet<Symbol>();
        foreach (var (sym, _) in bufferCands)
        {
            if (UsesAreSafe(body, sym, buffer: true) && CountFrees(body, sym) >= 1) { promoteBuffer.Add(sym); }
        }

        if (promoteStruct.Count == 0 && promoteBuffer.Count == 0) { return body; }

        // Retype each STRUCT-promoted symbol to the value type IN PLACE so every
        // shared VarRef reports the struct value type (drives the demoted decl).
        // Buffer symbols keep their pointer type — the stackalloc still yields a `T*`.
        foreach (var sym in promoteStruct) { sym.Type = structCands[sym]; }

        var bufInfo = bufferCands.Where(kv => promoteBuffer.Contains(kv.Key))
                                 .ToDictionary(kv => kv.Key, kv => kv.Value);
        var ctx = new PromoteCtx(promoteStruct, promoteBuffer, bufInfo);
        return (Block)RewriteMallocStmt(body, ctx)!;
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
        if (arg is not SizeOfExpr so || !so.Of.Unqualified.Equals(pt.Unqualified)) { return false; }
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
            case SetjmpCapture sc: CollectMallocDecls(sc.Body, into); break;
        }
    }

    /// <summary>Does <paramref name="init"/> match <c>[(char*)] malloc(N)</c> for a
    /// char-family pointer with a COMPILE-TIME CONSTANT byte count N in
    /// <c>(0, MaxStackPromoteBytes]</c>? Returns the unqualified element type (the
    /// char-family pointee, which lowers to C# <c>byte</c>) and the count. V1 is
    /// restricted to a 1-byte char element so the byte count IS the element count —
    /// no division, no "is it a clean multiple of sizeof(elem)" question. Wider
    /// elements (`int* = malloc(40)`) and computed sizes (`n * sizeof(T)`) are a
    /// follow-up.</summary>
    private static bool IsMallocOfByteBuffer(Symbol sym, CExpr? init, out CType elem, out long count)
    {
        elem = CType.Char;
        count = 0;
        if (sym.Type.Unqualified is not CType.Pointer { Pointee: var pt }) { return false; }
        // 1-byte char family only — char / signed char / unsigned char all lower to
        // `byte`. `_Bool` is also 1 byte but lowers to CBool, so exclude it by name.
        if (pt.Unqualified is not CType.Prim { Bytes: 1, Integer: true, Name: "char" or "signed char" or "unsigned char" }) { return false; }
        if (init is null) { return false; }
        var call = Unparen(init);
        if (call is Cast c) { call = Unparen(c.Operand); }
        if (call is not Call { Callee: "malloc", Args: { Count: 1 } args }) { return false; }
        var arg = Unparen(args[0]);
        if (arg is Cast ac) { arg = Unparen(ac.Operand); }
        if (arg is not LitInt { Value: { } n }) { return false; }
        if (n <= 0 || n > MaxStackPromoteBytes) { return false; }
        elem = pt.Unqualified;
        count = n;
        return true;
    }

    /// <summary>Collect buffer-promotion candidates. A candidate's <c>malloc</c> must
    /// run at most once on the stack, so a declaration <paramref name="inLoop"/>
    /// (inside any <c>while</c>/<c>for</c>/<c>do</c> body or a <c>for</c>-init) is
    /// excluded — a per-iteration <c>stackalloc</c> leaks a frame each pass (CA2014).
    /// Only a SOLE-declarator <c>DeclStmt</c> is taken so the whole statement can be
    /// swapped for one <see cref="ArrayDecl"/> without splitting a multi-declarator
    /// decl.</summary>
    private void CollectMallocBuffers(CStmt s, Dictionary<Symbol, BufferPromote> into, bool inLoop)
    {
        switch (s)
        {
            case Block b: foreach (var st in b.Stmts) { CollectMallocBuffers(st, into, inLoop); } break;
            case Seq q: foreach (var st in q.Stmts) { CollectMallocBuffers(st, into, inLoop); } break;
            case DeclStmt d when !inLoop && d.Decls.Count == 1:
                if (IsMallocOfByteBuffer(d.Decls[0].Sym, d.Decls[0].Init, out var elem, out var count))
                {
                    into[d.Decls[0].Sym] = new BufferPromote(elem, count);
                }
                break;
            case If f: CollectMallocBuffers(f.Then, into, inLoop); if (f.Else is { } e) { CollectMallocBuffers(e, into, inLoop); } break;
            case While w: CollectMallocBuffers(w.Body, into, inLoop: true); break;
            case DoWhile dw: CollectMallocBuffers(dw.Body, into, inLoop: true); break;
            case For fr: if (fr.Init is { } fi) { CollectMallocBuffers(fi, into, inLoop: true); } CollectMallocBuffers(fr.Body, into, inLoop: true); break;
            case Labeled lb: CollectMallocBuffers(lb.Body, into, inLoop); break;
            case CaseLabelStmt cl: CollectMallocBuffers(cl.Body, into, inLoop); break;
            case Switch sw: foreach (var sec in sw.Sections) { foreach (var st in sec.Body) { CollectMallocBuffers(st, into, inLoop); } } break;
            case SetjmpGuard sj:
                if (sj.TryBody is { } tb) { CollectMallocBuffers(tb, into, inLoop); }
                if (sj.CatchBody is { } cb) { CollectMallocBuffers(cb, into, inLoop); }
                break;
            // A goto-restart capture body re-runs like a loop, so a stackalloc inside would
            // leak a frame per longjmp (CA2014) — collect its buffers as inLoop.
            case SetjmpCapture sc: CollectMallocBuffers(sc.Body, into, inLoop: true); break;
        }
    }

    // ---- escape analysis: every use of `sym` must be p->field or free(p) -----

    /// <summary>An expression is SAFE for promotion when every <see cref="VarRef"/>
    /// of <paramref name="sym"/> within it appears only in a use the candidate's
    /// promotion kind allows: for a STRUCT, the direct base of a <c>p-&gt;field</c>
    /// access; for a <paramref name="buffer"/>, the direct base of a <c>p[i]</c>
    /// subscript; for either, the sole argument of <c>free(p)</c>. Any other
    /// occurrence — a bare reference (so passing <c>p</c> to a function, returning
    /// it, taking its address, or even a null test like <c>p == NULL</c> all escape)
    /// or any node shape this doesn't model — is unsafe (conservative, so a
    /// promotion never dangles).</summary>
    private static bool SafeExpr(CExpr? e, Symbol sym, bool buffer = false) => e switch
    {
        null => true,
        LitInt or LitFloat or LitStr or EnumConstRef or NullPtr or NameRef or SizeOfExpr or OffsetOf or DefaultLit or StackNew => true,
        VarRef v => v.Sym != sym,   // a bare reference to the candidate is an escape
        // p->field (struct): the base is the candidate itself — allowed, consumed here.
        Member { Arrow: true } m when !buffer && Unparen(m.Base) is VarRef bv && bv.Sym == sym => true,
        Member m => SafeExpr(m.Base, sym, buffer),
        // p[i] (buffer): the candidate as a subscript base — allowed, consumed here;
        // the index is checked but must not itself reference the candidate.
        Index { Base: var ib } ix when buffer && Unparen(ib) is VarRef iv && iv.Sym == sym => SafeExpr(ix.Idx, sym, buffer),
        // free(p): the candidate as free's sole arg — allowed, consumed here.
        Call { Callee: "free", Args: { Count: 1 } fa } when Unparen(fa[0]) is VarRef fv && fv.Sym == sym => true,
        Call c => c.Args.All(a => SafeExpr(a, sym, buffer)),
        IndirectCall ic => SafeExpr(ic.Callee, sym, buffer) && ic.Args.All(a => SafeExpr(a, sym, buffer)),
        Unary u => SafeExpr(u.Operand, sym, buffer),
        Binary b => SafeExpr(b.Left, sym, buffer) && SafeExpr(b.Right, sym, buffer),
        Assign a => SafeExpr(a.Target, sym, buffer) && SafeExpr(a.Value, sym, buffer),
        Cast c => SafeExpr(c.Operand, sym, buffer),
        CondExpr t => SafeExpr(t.Cond, sym, buffer) && SafeExpr(t.Then, sym, buffer) && SafeExpr(t.Else, sym, buffer),
        Index ix => SafeExpr(ix.Base, sym, buffer) && SafeExpr(ix.Idx, sym, buffer),
        CommaSeq cs => cs.Items.All(i => SafeExpr(i, sym, buffer)),
        CommaOp co => co.Items.All(i => SafeExpr(i, sym, buffer)),
        Paren p => SafeExpr(p.Inner, sym, buffer),
        VaArgGet va => SafeExpr(va.Ap, sym, buffer),
        StructInit si => si.Members.All(m => SafeExpr(m.Value, sym, buffer)),
        StackArray sa => sa.Elems.All(x => SafeExpr(x, sym, buffer)),
        PinnedArray pa => (pa.Elems?.All(x => SafeExpr(x, sym, buffer)) ?? true) && (pa.Count is null || SafeExpr(pa.Count, sym, buffer)),
        _ => false,   // an unmodeled node: conservatively unsafe
    };

    private static bool UsesAreSafe(CStmt s, Symbol sym, bool buffer = false) => s switch
    {
        Block b => b.Stmts.All(x => UsesAreSafe(x, sym, buffer)),
        Seq q => q.Stmts.All(x => UsesAreSafe(x, sym, buffer)),
        DeclStmt d => d.Decls.All(ld => SafeExpr(ld.Init, sym, buffer)),
        // free(p) as a statement is allowed (it will be dropped); other expr-stmts
        // must be safe.
        ExprStmt es => IsFreeOf(es.Expr, sym) || SafeExpr(es.Expr, sym, buffer),
        If f => SafeExpr(f.Cond, sym, buffer) && UsesAreSafe(f.Then, sym, buffer) && (f.Else is null || UsesAreSafe(f.Else, sym, buffer)),
        While w => SafeExpr(w.Cond, sym, buffer) && UsesAreSafe(w.Body, sym, buffer),
        DoWhile dw => UsesAreSafe(dw.Body, sym, buffer) && SafeExpr(dw.Cond, sym, buffer),
        For fr => (fr.Init is null || UsesAreSafe(fr.Init, sym, buffer)) && SafeExpr(fr.Cond, sym, buffer)
                  && SafeExpr(fr.Post, sym, buffer) && UsesAreSafe(fr.Body, sym, buffer),
        Return r => SafeExpr(r.Value, sym, buffer),
        Break or Continue or Goto => true,
        Labeled lb => UsesAreSafe(lb.Body, sym, buffer),
        CaseLabelStmt cl => SafeExpr(cl.CaseExpr, sym, buffer) && UsesAreSafe(cl.Body, sym, buffer),
        Switch sw => SafeExpr(sw.Subject, sym, buffer) && sw.Sections.All(sec => sec.Body.All(st => UsesAreSafe(st, sym, buffer))),
        SetjmpGuard sj => SafeExpr(sj.Env, sym, buffer) && (sj.TryBody is null || UsesAreSafe(sj.TryBody, sym, buffer))
                          && (sj.CatchBody is null || UsesAreSafe(sj.CatchBody, sym, buffer)),
        SetjmpCapture sc => SafeExpr(sc.Env, sym, buffer) && SafeExpr(sc.Target, sym, buffer) && UsesAreSafe(sc.Body, sym, buffer),
        ArrayDecl ad => SafeExpr(ad.CountExpr, sym, buffer) && (ad.Inits?.All(x => SafeExpr(x, sym, buffer)) ?? true),
        _ => false,
    };

    private static bool IsFreeOf(CExpr e, Symbol sym) =>
        Unparen(e) is Call { Callee: "free", Args: { Count: 1 } a } && Unparen(a[0]) is VarRef v && v.Sym == sym;

    /// <summary>The symbol passed to <c>free(p)</c> when <paramref name="e"/> is
    /// exactly that call (else null) — lets the rewrite ask <see cref="PromoteCtx.IsFreeable"/>
    /// directly instead of scanning both promotion sets.</summary>
    private static Symbol? FreedSymbol(CExpr e) =>
        Unparen(e) is Call { Callee: "free", Args: { Count: 1 } a } && Unparen(a[0]) is VarRef v ? v.Sym : null;

    private static int CountFrees(CStmt s, Symbol sym)
    {
        var n = 0;
        void E(CExpr? e) { if (e is not null && IsFreeOf(e, sym)) { n++; } }
        void S(CStmt st)
        {
            switch (st)
            {
                case Block b: foreach (var x in b.Stmts) { S(x); } break;
                case Seq q: foreach (var x in q.Stmts) { S(x); } break;
                case ExprStmt es: E(es.Expr); break;
                case If f: S(f.Then); if (f.Else is { } e) { S(e); } break;
                case While w: S(w.Body); break;
                case DoWhile dw: S(dw.Body); break;
                case For fr: if (fr.Init is { } fi) { S(fi); } S(fr.Body); break;
                case Labeled lb: S(lb.Body); break;
                case CaseLabelStmt cl: S(cl.Body); break;
                case Switch sw: foreach (var sec in sw.Sections) { foreach (var x in sec.Body) { S(x); } } break;
                case SetjmpGuard sj: if (sj.TryBody is { } tb) { S(tb); } if (sj.CatchBody is { } cb) { S(cb); } break;
                case SetjmpCapture sc: S(sc.Body); break;
            }
        }
        S(s);
        return n;
    }

    // ---- rewrite: demote decl, arrow→dot, drop free ------------------------
    //
    // Expression rewriting only touches STRUCT-promoted symbols (the arrow→dot of a
    // retyped value). Buffer-promoted symbols keep their pointer type and their
    // subscripts unchanged, so the buffer path lives entirely at the statement level
    // (decl swap + free drop) and needs no expression rewrite.

    private CExpr RewriteMallocExpr(CExpr e, PromoteCtx ctx) => e switch
    {
        // p->field → p.field (the symbol is already retyped to the struct value).
        Member { Arrow: true } m when Unparen(m.Base) is VarRef bv && ctx.Struct.Contains(bv.Sym)
            => m with { Base = RewriteMallocExpr(m.Base, ctx), Arrow = false },
        Member m => m with { Base = RewriteMallocExpr(m.Base, ctx) },
        Unary u => u with { Operand = RewriteMallocExpr(u.Operand, ctx) },
        Binary b => b with { Left = RewriteMallocExpr(b.Left, ctx), Right = RewriteMallocExpr(b.Right, ctx) },
        Assign a => a with { Target = RewriteMallocExpr(a.Target, ctx), Value = RewriteMallocExpr(a.Value, ctx) },
        Call c => c with { Args = c.Args.Select(x => RewriteMallocExpr(x, ctx)).ToList() },
        IndirectCall ic => ic with { Callee = RewriteMallocExpr(ic.Callee, ctx), Args = ic.Args.Select(x => RewriteMallocExpr(x, ctx)).ToList() },
        Cast c => c with { Operand = RewriteMallocExpr(c.Operand, ctx) },
        CondExpr t => t with { Cond = RewriteMallocExpr(t.Cond, ctx), Then = RewriteMallocExpr(t.Then, ctx), Else = RewriteMallocExpr(t.Else, ctx) },
        Index ix => ix with { Base = RewriteMallocExpr(ix.Base, ctx), Idx = RewriteMallocExpr(ix.Idx, ctx) },
        CommaSeq cs => cs with { Items = cs.Items.Select(x => RewriteMallocExpr(x, ctx)).ToList() },
        CommaOp co => co with { Items = co.Items.Select(x => RewriteMallocExpr(x, ctx)).ToList() },
        Paren p => p with { Inner = RewriteMallocExpr(p.Inner, ctx) },
        VaArgGet va => va with { Ap = RewriteMallocExpr(va.Ap, ctx) },
        StructInit si => si with { Members = si.Members.Select(m => m with { Value = RewriteMallocExpr(m.Value, ctx) }).ToList() },
        _ => e,
    };

    private CExpr? RewriteMallocExprN(CExpr? e, PromoteCtx ctx) => e is null ? null : RewriteMallocExpr(e, ctx);

    /// <summary>Build the <see cref="ArrayDecl"/> a buffer-promoted decl becomes:
    /// <c>byte* p = stackalloc byte[N]</c> (zeroed — over-satisfying malloc's
    /// uninitialized memory, which any correct program writes before reading).</summary>
    private static ArrayDecl BufferStackalloc(LocalDecl ld, BufferPromote bp, SrcPos pos)
    {
        var countLit = new LitInt(bp.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), bp.Count)
        {
            Type = CType.Int,
            Pos = ld.Init?.Pos ?? pos,
        };
        return new ArrayDecl(ld.Sym, bp.Element, countLit, null) { Pos = pos };
    }

    /// <summary>Rewrite a statement; returns null when it should be DROPPED (a
    /// promoted <c>free(p)</c>).</summary>
    private CStmt? RewriteMallocStmt(CStmt s, PromoteCtx ctx)
    {
        switch (s)
        {
            case Block b:
                return b with { Stmts = b.Stmts.Select(x => RewriteMallocStmt(x, ctx)).Where(x => x is not null).Select(x => x!).ToList() };
            case Seq q:
                return q with { Stmts = q.Stmts.Select(x => RewriteMallocStmt(x, ctx)).Where(x => x is not null).Select(x => x!).ToList() };
            case ExprStmt es when FreedSymbol(es.Expr) is { } fs && ctx.IsFreeable(fs):
                return null;   // drop the free of a promoted pointer
            // A sole-declarator buffer malloc → a stackalloc ArrayDecl (the whole
            // DeclStmt is replaced; CollectMallocBuffers only took Decls.Count == 1).
            case DeclStmt d when d.Decls.Count == 1 && ctx.Buffer.Contains(d.Decls[0].Sym):
                return BufferStackalloc(d.Decls[0], ctx.BufInfo[d.Decls[0].Sym], d.Pos);
            case DeclStmt d:
                return d with { Decls = d.Decls.Select(ld =>
                    ctx.Struct.Contains(ld.Sym)
                        ? ld with { Init = new StackNew(ld.Sym.Type) { Type = ld.Sym.Type, Pos = ld.Init?.Pos ?? default } }
                        : ld with { Init = RewriteMallocExprN(ld.Init, ctx) }).ToList() };
            case ExprStmt es: return es with { Expr = RewriteMallocExpr(es.Expr, ctx) };
            case If f: return f with { Cond = RewriteMallocExpr(f.Cond, ctx), Then = RewriteMallocStmt(f.Then, ctx)!, Else = f.Else is { } e ? RewriteMallocStmt(e, ctx) : null };
            case While w: return w with { Cond = RewriteMallocExpr(w.Cond, ctx), Body = RewriteMallocStmt(w.Body, ctx)! };
            case DoWhile dw: return dw with { Body = RewriteMallocStmt(dw.Body, ctx)!, Cond = RewriteMallocExpr(dw.Cond, ctx) };
            case For fr: return fr with { Init = fr.Init is { } fi ? RewriteMallocStmt(fi, ctx) : null, Cond = RewriteMallocExprN(fr.Cond, ctx), Post = RewriteMallocExprN(fr.Post, ctx), Body = RewriteMallocStmt(fr.Body, ctx)! };
            case Return r: return r with { Value = RewriteMallocExprN(r.Value, ctx) };
            case Labeled lb: return lb with { Body = RewriteMallocStmt(lb.Body, ctx)! };
            case CaseLabelStmt cl: return cl with { CaseExpr = RewriteMallocExprN(cl.CaseExpr, ctx), Body = RewriteMallocStmt(cl.Body, ctx)! };
            case Switch sw: return sw with { Subject = RewriteMallocExpr(sw.Subject, ctx), Sections = sw.Sections.Select(sec => sec with { Body = sec.Body.Select(x => RewriteMallocStmt(x, ctx)).Where(x => x is not null).Select(x => x!).ToList() }).ToList() };
            case SetjmpGuard sj: return sj with { Env = RewriteMallocExpr(sj.Env, ctx), TryBody = sj.TryBody is { } tb ? RewriteMallocStmt(tb, ctx) : null, CatchBody = sj.CatchBody is { } cb ? RewriteMallocStmt(cb, ctx) : null };
            case SetjmpCapture sc: return sc with { Env = RewriteMallocExpr(sc.Env, ctx), Target = RewriteMallocExpr(sc.Target, ctx), Body = RewriteMallocStmt(sc.Body, ctx)! };
            case ArrayDecl ad: return ad with { CountExpr = RewriteMallocExprN(ad.CountExpr, ctx), Inits = ad.Inits?.Select(x => RewriteMallocExpr(x, ctx)).ToList() };
            default: return s;
        }
    }
}
