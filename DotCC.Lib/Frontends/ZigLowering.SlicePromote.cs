#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using Index = DotCC.Ir.Index;   // disambiguate the IR subscript node from System.Index

namespace DotCC.Frontends;

internal sealed partial class ZigLowering
{
    // ---- non-escaping stack-slice peephole (Milestone O, part 5) -------------
    //
    // The Zig analogue of the C front-end's malloc→stackalloc promotion
    // ([[malloc-stackalloc-promote]] in IrBuilder.MallocPromote.cs). A heap slice
    // allocated through the DEVIRTUALIZED C-heap default allocator
    // (`std.heap.page_allocator` / `c_allocator`, whose `.alloc` lowers to a direct
    // `ZigAlloc.AllocCHeap` → `Libc.malloc`) is demoted to a `stackalloc` backing
    // when escape analysis proves it never leaves the stack frame:
    //
    //     const s = try a.alloc(u8, N);   // ZigTry(AllocCall(Receiver: null, …))
    //     …use s only via s[i] / s.len…
    //     a.free(s);                       // FreeCall(Receiver: null, …)
    //   ⇒
    //     byte* __slicebufK = stackalloc byte[N];     // ArrayDecl (zeroed)
    //     Slice<byte> s = new Slice<byte>(__slicebufK, (ulong)N);   // SliceNew
    //     // the free is dropped
    //
    // The slice symbol KEEPS its `CType.Slice` type, so every `s[i]` / `s.len`
    // stays valid with no expression rewrite — the transform lives entirely at the
    // statement level (decl swap + free drop), exactly like the malloc BUFFER arm.
    // This is the zero-heap win the slices design ([[zig-slices-design]]) parked: a
    // local `[N]T` array is already stack-backed, so only an ALLOCATOR-backed slice
    // has a real heap allocation to eliminate.
    //
    // V1 is deliberately narrow and conservative (a missed promotion is harmless; a
    // wrong one would dangle):
    //   * only the DEVIRTUALIZED C-heap default (`AllocCall.Receiver == null`) — the
    //     indirect/FBA path is either already stack-backed or a runtime-chosen
    //     allocator we don't second-guess;
    //   * only `try a.alloc(…)` (the `catch` form and `defer a.free(…)` are cuts);
    //   * a 1-byte (byte-family) element, so the byte count IS the element count;
    //   * a compile-time-constant count in (0, MaxStackSliceBytes];
    //   * the decl NOT inside a loop (a per-iteration stackalloc leaks a frame);
    //   * every use of the slice is `s[i]`, `s.len`, or `a.free(s)` — a bare
    //     reference, `s.ptr`, a store-to-`s`, a return, or passing it to a callee all
    //     escape and disqualify.

    /// <summary>The byte ceiling for slice promotion (mirrors the malloc arm's
    /// <c>MaxStackPromoteBytes</c>): a constant-size alloc above this stays on the
    /// heap. `alloc(huge)` returns a recoverable OOM error; `stackalloc huge` is an
    /// unrecoverable overflow, so the transform fires only on a bounded, modest size.</summary>
    private const long MaxStackSliceBytes = 1024;

    /// <summary>Monotonic counter for synthetic stackalloc backing-buffer names
    /// (<c>__slicebufK</c>), unique within a function (the symbol table also
    /// uniquifies, but a distinct seed keeps the emitted names readable).</summary>
    private int _sliceBufSeq;

    /// <summary>Promotion bookkeeping for one promoted slice symbol: the stackalloc
    /// element type (a 1-byte char-family pointee → C# <c>byte</c>) and the constant
    /// element count.</summary>
    private readonly record struct SlicePromote(CType Element, long Count);

    /// <summary>Run the stack-slice peephole over a function body, returning the
    /// rewritten block (or the original when nothing is promotable).</summary>
    private Block PromoteStackSlices(Block body)
    {
        var cands = new Dictionary<Symbol, SlicePromote>();
        CollectSliceAllocs(body, cands, inLoop: false);
        if (cands.Count == 0) { return body; }

        var promote = new Dictionary<Symbol, SlicePromote>();
        foreach (var (sym, info) in cands)
        {
            if (SliceUsesAreSafe(body, sym) && CountSliceFrees(body, sym) >= 1)
            {
                promote[sym] = info;
            }
        }
        if (promote.Count == 0) { return body; }

        return (Block)RewriteSliceStmt(body, promote)!;
    }

    /// <summary>Does <paramref name="init"/> match <c>try a.alloc(elem, N)</c> for a
    /// DEVIRTUALIZED C-heap default allocator (<c>AllocCall.Receiver == null</c>) with
    /// a 1-byte char-family element and a compile-time-constant count
    /// <c>N ∈ (0, MaxStackSliceBytes]</c>? The declared symbol must itself be a slice.
    /// Returns the element type and count.</summary>
    private static bool IsStackPromotableSliceAlloc(Symbol sym, CExpr? init, out CType elem, out long count)
    {
        elem = CType.UChar;
        count = 0;
        if (sym.Type.Unqualified is not CType.Slice) { return false; }
        // `const s = try a.alloc(…)` lowers to `ZigTry(AllocCall(…))` (the error union
        // unwrapped). A bare `a.alloc(…)` without `try`/`catch` would leave the error
        // union unbound, so the `try` is the only shape that binds a usable slice.
        if (init is not ZigTry { Inner: AllocCall ac }) { return false; }
        if (ac.Receiver is not null || ac.FbaCtx is not null) { return false; }   // indirect / FBA-devirt stays on its allocator
        // 1-byte char family only (u8 → unsigned char, i8 → signed char) — the byte
        // count IS the element count, so no division / clean-multiple question.
        if (ac.Element.Unqualified is not CType.Prim { Bytes: 1, Integer: true, Name: "char" or "signed char" or "unsigned char" }) { return false; }
        if (ac.Count is not LitInt { Value: { } n }) { return false; }
        if (n <= 0 || n > MaxStackSliceBytes) { return false; }
        elem = ac.Element.Unqualified;
        count = n;
        return true;
    }

    /// <summary>Collect slice-promotion candidates. A candidate's <c>alloc</c> must run
    /// at most once on the stack, so a declaration <paramref name="inLoop"/> is excluded
    /// (a per-iteration <c>stackalloc</c> leaks a frame each pass, CA2014). Only a
    /// SOLE-declarator <c>DeclStmt</c> is taken so the whole statement can be swapped for
    /// the two-statement <c>stackalloc</c>+<c>SliceNew</c> sequence.</summary>
    private void CollectSliceAllocs(CStmt s, Dictionary<Symbol, SlicePromote> into, bool inLoop)
    {
        switch (s)
        {
            case Block b: foreach (var st in b.Stmts) { CollectSliceAllocs(st, into, inLoop); } break;
            case Seq q: foreach (var st in q.Stmts) { CollectSliceAllocs(st, into, inLoop); } break;
            case DeclStmt d when !inLoop && d.Decls.Count == 1:
                if (IsStackPromotableSliceAlloc(d.Decls[0].Sym, d.Decls[0].Init, out var elem, out var count))
                {
                    into[d.Decls[0].Sym] = new SlicePromote(elem, count);
                }
                break;
            case If f: CollectSliceAllocs(f.Then, into, inLoop); if (f.Else is { } e) { CollectSliceAllocs(e, into, inLoop); } break;
            case While w: CollectSliceAllocs(w.Body, into, inLoop: true); break;
            case DoWhile dw: CollectSliceAllocs(dw.Body, into, inLoop: true); break;
            case For fr: if (fr.Init is { } fi) { CollectSliceAllocs(fi, into, inLoop: true); } CollectSliceAllocs(fr.Body, into, inLoop: true); break;
            case Labeled lb: CollectSliceAllocs(lb.Body, into, inLoop); break;
            case CaseLabelStmt cl: CollectSliceAllocs(cl.Body, into, inLoop); break;
            case Switch sw: foreach (var sec in sw.Sections) { foreach (var st in sec.Body) { CollectSliceAllocs(st, into, inLoop); } } break;
            case SetjmpGuard sj:
                if (sj.TryBody is { } tb) { CollectSliceAllocs(tb, into, inLoop); }
                if (sj.CatchBody is { } cb) { CollectSliceAllocs(cb, into, inLoop); }
                break;
            // C-only node (Zig never produces it), handled for node-coverage symmetry.
            case SetjmpCapture sc: CollectSliceAllocs(sc.Body, into, inLoop: true); break;
        }
    }

    // ---- escape analysis: every use of `sym` must be s[i], s.len, or free(s) ----

    /// <summary>An expression is SAFE for promotion when every reference to
    /// <paramref name="sym"/> within it appears only in a use the slice promotion
    /// allows: the base of a <c>s[i]</c> subscript, the base of an <c>s.len</c> read
    /// (a copied <c>ulong</c>, no aliasing), or the slice argument of <c>a.free(s)</c>.
    /// Any other occurrence — a bare reference (returning <c>s</c>, storing it, passing
    /// it to a callee), an <c>s.ptr</c> read (exposes the backing pointer), or any node
    /// shape this doesn't model — is unsafe (conservative, so a promotion never
    /// dangles).</summary>
    private static bool SliceSafeExpr(CExpr? e, Symbol sym) => e switch
    {
        null => true,
        LitInt or LitFloat or LitStr or LitBool or EnumConstRef or NullPtr or NameRef or SizeOfExpr or DefaultLit => true,
        VarRef v => v.Sym != sym,   // a bare reference to the candidate is an escape
        // s[i] lowers to `s.Ptr[i]` = Index{ Base: Member{Field:"Ptr", Base: s}, Idx }. The
        // `.Ptr` is consumed AS the subscript base — allowed; the index must not itself reference
        // the candidate. (A bare `s.Ptr` NOT immediately indexed falls through to the Member rule
        // below → escape, since it exposes the backing pointer.)
        Index { Base: var ib } ix when IsSlicePtrOf(ib, sym) => SliceSafeExpr(ix.Idx, sym),
        // a hypothetical bare `s[i]` (a slice never lowers to this — its subscript always goes
        // through `.Ptr` above — but harmless to allow the consumed-base form).
        Index { Base: var ib2 } ix2 when Unparen(ib2) is VarRef iv && iv.Sym == sym => SliceSafeExpr(ix2.Idx, sym),
        // s.len: a numeric read (the runtime Slice<T>'s `Len`) — allowed, consumed here.
        // `.ptr` (Field "Ptr") is deliberately NOT consumed here (only as an index base above):
        // a standalone `.ptr` exposes the backing pointer, so it escapes via the Member rule.
        Member { Field: "Len", Base: var mb } when Unparen(mb) is VarRef mv && mv.Sym == sym => true,
        Member m => SliceSafeExpr(m.Base, sym),
        // a.free(s): the candidate as the devirtualized free's slice arg — allowed.
        FreeCall { Receiver: null, FbaCtx: null, SliceExpr: var fe } when Unparen(fe) is VarRef fv && fv.Sym == sym => true,
        FreeCall fc => SliceSafeExpr(fc.Receiver, sym) && SliceSafeExpr(fc.SliceExpr, sym),
        AllocCall ac => SliceSafeExpr(ac.Receiver, sym) && SliceSafeExpr(ac.Count, sym),
        ZigTry zt => SliceSafeExpr(zt.Inner, sym),
        ZigCatch zc => SliceSafeExpr(zc.Union, sym) && SliceSafeExpr(zc.Fallback, sym),
        SliceNew sn => SliceSafeExpr(sn.Ptr, sym) && SliceSafeExpr(sn.Len, sym),
        // `return e;` in a `!T` fn wraps `e` as ErrUnion.Ok(payload). Recursing the payload keeps
        // the escape sound: `return s;` is `ErrUnionOk(VarRef s)` → the bare ref below → escape.
        ErrUnionOk ok => SliceSafeExpr(ok.Payload, sym),
        ErrUnionErr => true,
        TupleNew tn => tn.Elements.All(x => SliceSafeExpr(x, sym)),
        TupleIndex ti => SliceSafeExpr(ti.Tuple, sym),
        Index ix => SliceSafeExpr(ix.Base, sym) && SliceSafeExpr(ix.Idx, sym),
        Unary u => SliceSafeExpr(u.Operand, sym),
        Binary b => SliceSafeExpr(b.Left, sym) && SliceSafeExpr(b.Right, sym),
        Assign a => SliceSafeExpr(a.Target, sym) && SliceSafeExpr(a.Value, sym),
        Cast c => SliceSafeExpr(c.Operand, sym),
        CondExpr t => SliceSafeExpr(t.Cond, sym) && SliceSafeExpr(t.Then, sym) && SliceSafeExpr(t.Else, sym),
        Call c => c.Args.All(a => SliceSafeExpr(a, sym)),
        IndirectCall ic => SliceSafeExpr(ic.Callee, sym) && ic.Args.All(a => SliceSafeExpr(a, sym)),
        _ => false,   // an unmodeled node: conservatively unsafe
    };

    private static bool SliceUsesAreSafe(CStmt s, Symbol sym) => s switch
    {
        Block b => b.Stmts.All(x => SliceUsesAreSafe(x, sym)),
        Seq q => q.Stmts.All(x => SliceUsesAreSafe(x, sym)),
        DeclStmt d => d.Decls.All(ld => SliceSafeExpr(ld.Init, sym)),
        // a.free(s) as a statement is allowed (it will be dropped); other expr-stmts
        // must be safe.
        ExprStmt es => IsFreeOfSlice(es.Expr, sym) || SliceSafeExpr(es.Expr, sym),
        If f => SliceSafeExpr(f.Cond, sym) && SliceUsesAreSafe(f.Then, sym) && (f.Else is null || SliceUsesAreSafe(f.Else, sym)),
        While w => SliceSafeExpr(w.Cond, sym) && SliceUsesAreSafe(w.Body, sym),
        DoWhile dw => SliceUsesAreSafe(dw.Body, sym) && SliceSafeExpr(dw.Cond, sym),
        For fr => (fr.Init is null || SliceUsesAreSafe(fr.Init, sym)) && SliceSafeExpr(fr.Cond, sym)
                  && SliceSafeExpr(fr.Post, sym) && SliceUsesAreSafe(fr.Body, sym),
        Return r => SliceSafeExpr(r.Value, sym),
        Break or Continue or Goto => true,
        Labeled lb => SliceUsesAreSafe(lb.Body, sym),
        CaseLabelStmt cl => SliceSafeExpr(cl.CaseExpr, sym) && SliceUsesAreSafe(cl.Body, sym),
        Switch sw => SliceSafeExpr(sw.Subject, sym) && sw.Sections.All(sec => sec.Body.All(st => SliceUsesAreSafe(st, sym))),
        ArrayDecl ad => SliceSafeExpr(ad.CountExpr, sym) && (ad.Inits?.All(x => SliceSafeExpr(x, sym)) ?? true),
        _ => false,   // an unmodeled statement (e.g. a defer guard): conservatively unsafe
    };

    /// <summary>True when <paramref name="e"/> is <c>sym.Ptr</c> (the slice's backing-pointer
    /// member) — the shape a slice subscript <c>s[i]</c> lowers to as an index base.</summary>
    private static bool IsSlicePtrOf(CExpr e, Symbol sym) =>
        Unparen(e) is Member { Field: "Ptr", Base: var b } && Unparen(b) is VarRef v && v.Sym == sym;

    /// <summary>True when <paramref name="e"/> is exactly the devirtualized
    /// <c>a.free(sym)</c> (so the statement can be dropped on promotion).</summary>
    private static bool IsFreeOfSlice(CExpr e, Symbol sym) =>
        Unparen(e) is FreeCall { Receiver: null, FbaCtx: null, SliceExpr: var fe } && Unparen(fe) is VarRef v && v.Sym == sym;

    private static int CountSliceFrees(CStmt s, Symbol sym)
    {
        var n = 0;
        void E(CExpr? e) { if (e is not null && IsFreeOfSlice(e, sym)) { n++; } }
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
            }
        }
        S(s);
        return n;
    }

    // ---- rewrite: decl → stackalloc + SliceNew, drop free -------------------
    //
    // No expression rewrite is needed — a promoted slice keeps its `CType.Slice`
    // type, so its `s[i]` / `s.len` uses stay valid. The transform is purely at the
    // statement level: replace the candidate decl with a brace-less Seq of the
    // backing `stackalloc` + the `SliceNew`, and drop the promoted free statements.

    /// <summary>Build the two-statement sequence a promoted slice decl becomes:
    /// <c>byte* __slicebufK = stackalloc byte[N];</c> then
    /// <c>Slice&lt;byte&gt; s = new Slice&lt;byte&gt;(__slicebufK, (ulong)N);</c>. Emitted
    /// as a brace-less <see cref="Seq"/> so both land in the enclosing block scope.</summary>
    private Seq BuildPromotedSliceDecl(LocalDecl ld, SlicePromote sp, SrcPos pos)
    {
        var inv = CultureInfo.InvariantCulture;
        var backing = _symbols.Declare(new Symbol
        {
            Name = "__slicebuf" + _sliceBufSeq++, Kind = SymKind.Var, Type = new CType.Pointer(sp.Element),
        });
        var countInt = new LitInt(sp.Count.ToString(inv), sp.Count) { Type = CType.Int, Pos = pos };
        var arrDecl = new ArrayDecl(backing, sp.Element, countInt, null) { Pos = pos };

        var isConst = ld.Sym.Type.Unqualified is CType.Slice { Element: var el } && el.IsConst;
        var ptr = new VarRef(backing) { Type = backing.Type, Pos = pos };
        var lenUL = new LitInt(sp.Count.ToString(inv), sp.Count) { Type = CType.ULong, Pos = pos };
        var sliceNew = new SliceNew(ptr, lenUL, sp.Element, isConst) { Type = ld.Sym.Type, Pos = pos };
        var sliceDecl = new DeclStmt(new List<LocalDecl> { new(ld.Sym, sliceNew) }) { Pos = pos };

        return new Seq(new List<CStmt> { arrDecl, sliceDecl }) { Pos = pos };
    }

    /// <summary>Rewrite a statement; returns null when it should be DROPPED (a promoted
    /// <c>a.free(s)</c>). A candidate decl becomes the <c>stackalloc</c>+<c>SliceNew</c>
    /// sequence; everything else recurses structurally (no expression rewrite needed).</summary>
    private CStmt? RewriteSliceStmt(CStmt s, Dictionary<Symbol, SlicePromote> promote)
    {
        switch (s)
        {
            case Block b:
                return b with { Stmts = b.Stmts.Select(x => RewriteSliceStmt(x, promote)).Where(x => x is not null).Select(x => x!).ToList() };
            case Seq q:
                return q with { Stmts = q.Stmts.Select(x => RewriteSliceStmt(x, promote)).Where(x => x is not null).Select(x => x!).ToList() };
            case ExprStmt es when Unparen(es.Expr) is FreeCall { Receiver: null, FbaCtx: null, SliceExpr: var fe }
                                  && Unparen(fe) is VarRef v && promote.ContainsKey(v.Sym):
                return null;   // drop the free of a promoted slice
            case DeclStmt d when d.Decls.Count == 1 && promote.TryGetValue(d.Decls[0].Sym, out var sp):
                return BuildPromotedSliceDecl(d.Decls[0], sp, d.Pos);
            case If f: return f with { Then = RewriteSliceStmt(f.Then, promote)!, Else = f.Else is { } e ? RewriteSliceStmt(e, promote) : null };
            case While w: return w with { Body = RewriteSliceStmt(w.Body, promote)! };
            case DoWhile dw: return dw with { Body = RewriteSliceStmt(dw.Body, promote)! };
            case For fr: return fr with { Init = fr.Init is { } fi ? RewriteSliceStmt(fi, promote) : null, Body = RewriteSliceStmt(fr.Body, promote)! };
            case Labeled lb: return lb with { Body = RewriteSliceStmt(lb.Body, promote)! };
            case CaseLabelStmt cl: return cl with { Body = RewriteSliceStmt(cl.Body, promote)! };
            case Switch sw: return sw with { Sections = sw.Sections.Select(sec => sec with { Body = sec.Body.Select(x => RewriteSliceStmt(x, promote)).Where(x => x is not null).Select(x => x!).ToList() }).ToList() };
            case SetjmpGuard sj: return sj with { TryBody = sj.TryBody is { } tb ? RewriteSliceStmt(tb, promote) : null, CatchBody = sj.CatchBody is { } cb ? RewriteSliceStmt(cb, promote) : null };
            case SetjmpCapture sc: return sc with { Body = RewriteSliceStmt(sc.Body, promote)! };
            default: return s;
        }
    }
}
