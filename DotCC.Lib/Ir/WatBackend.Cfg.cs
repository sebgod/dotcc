#nullable enable

using System.Collections.Generic;
using System.Linq;

namespace DotCC.Ir;

/// <summary>
/// The control-flow-graph dispatch-loop lowering for functions that use
/// <c>goto</c>/labels. wasm has no arbitrary jump — only structured
/// <c>block</c>/<c>loop</c>/<c>br</c> — so a function whose control flow is not
/// expressible structurally is compiled by building a basic-block CFG and emitting
/// it as a single dispatch loop:
/// <code>
///   (local $__lbl i32)
///   i32.const &lt;entry&gt;  local.set $__lbl
///   loop $__disp
///     block $cfb{m-1} … block $cfb0
///       local.get $__lbl
///       br_table $cfb0 … $cfb{m-1} $cfb0
///     end                       ;; control for block 0 lands here
///     &lt;block 0 code&gt; &lt;block 0 transfer: set $__lbl; br $__disp | return&gt;
///     end                       ;; block 1 …
///     …
///   end
///   unreachable                 ;; every block transfers, so the loop never falls out
/// </code>
/// This is correct for ANY control-flow graph — forward and backward gotos, labels
/// at any nesting depth, and even irreducible graphs — at the cost of a
/// <c>br_table</c> per block transition. Functions WITHOUT labels keep the clean
/// structured emit; only goto-using functions pay this. (Producing nicer nested
/// structure here — a relooper — is a possible later quality pass; it has no
/// correctness stakes, since this lowering is already correct.)
/// <para>A <c>switch</c> nested inside a goto-using function is not yet modelled in
/// the CFG (its multi-way dispatch would need synthetic-local plumbing); it fails
/// loud rather than miscompile. The two appearing together is rare.</para>
/// </summary>
internal sealed partial class WatBackend
{
    /// <summary>How a basic block transfers control once its straight-line code has run.</summary>
    private enum CfgKind { FallOff, Goto, Branch, Return }

    /// <summary>A CFG basic block: a run of straight-line statements
    /// (<see cref="DeclStmt"/>/<see cref="ArrayDecl"/>/<see cref="ExprStmt"/>) followed
    /// by exactly one control transfer. All structured control flow (if/while/for/…)
    /// is decomposed into edges between these.</summary>
    private sealed class CfgBlock
    {
        public int Index = -1;                       // dense id among the REACHABLE blocks
        public readonly List<CStmt> Code = new();
        public CfgKind Kind = CfgKind.FallOff;
        public CfgBlock? T;                          // goto target / branch-true target
        public CfgBlock? F;                          // branch-false target
        public CExpr? Cond;                          // branch condition
        public CExpr? RetVal;                        // return value (Kind == Return)
        public bool Terminated;                      // an explicit transfer has been set
    }

    // CFG-builder state (one function at a time).
    private List<CfgBlock> _cfgAll = new();
    private CfgBlock _cfgCur = null!;
    private readonly Dictionary<string, CfgBlock> _cfgLabels = new(System.StringComparer.Ordinal);
    private readonly List<(CfgBlock Brk, CfgBlock Cont)> _cfgLoops = new();

    /// <summary>True when a statement tree contains a labeled statement (a goto target)
    /// anywhere — the trigger for the CFG dispatch-loop lowering.</summary>
    private static bool ContainsLabel(IReadOnlyList<CStmt> stmts) => stmts.Any(StmtHasLabel);

    private static bool StmtHasLabel(CStmt s) => s switch
    {
        Labeled => true,
        Block b => b.Stmts.Any(StmtHasLabel),
        If i => StmtHasLabel(i.Then) || (i.Else is { } e && StmtHasLabel(e)),
        While w => StmtHasLabel(w.Body),
        DoWhile dw => StmtHasLabel(dw.Body),
        For f => (f.Init is { } init && StmtHasLabel(init)) || StmtHasLabel(f.Body),
        Switch sw => sw.Sections.Any(sec => sec.Body.Any(StmtHasLabel)),
        CaseLabelStmt cl => StmtHasLabel(cl.Body),
        _ => false,
    };

    // ---- CFG construction (AST -> basic blocks) --------------------------

    private CfgBlock NewCfgBlock()
    {
        var b = new CfgBlock();
        _cfgAll.Add(b);
        return b;
    }

    private CfgBlock LabelBlock(string name)
    {
        if (!_cfgLabels.TryGetValue(name, out var b)) { b = NewCfgBlock(); _cfgLabels[name] = b; }
        return b;
    }

    // Set a block's transfer, but only the FIRST one wins — code emitted after a
    // return/goto into the same builder block is dead and must not overwrite it.
    private static void LinkGoto(CfgBlock from, CfgBlock to)
    {
        if (from.Terminated) { return; }
        from.Kind = CfgKind.Goto; from.T = to; from.Terminated = true;
    }

    private static void BranchTo(CfgBlock from, CExpr cond, CfgBlock t, CfgBlock f)
    {
        if (from.Terminated) { return; }
        from.Kind = CfgKind.Branch; from.Cond = cond; from.T = t; from.F = f; from.Terminated = true;
    }

    private static void ReturnFrom(CfgBlock from, CExpr? value)
    {
        if (from.Terminated) { return; }
        from.Kind = CfgKind.Return; from.RetVal = value; from.Terminated = true;
    }

    private void BuildCfg(CStmt s)
    {
        switch (s)
        {
            case Block b:
                foreach (var inner in b.Stmts) { BuildCfg(inner); }
                break;

            // Straight-line statements accumulate into the current block.
            case DeclStmt:
            case ArrayDecl:
            case ExprStmt:
                _cfgCur.Code.Add(s);
                break;

            case If i:
            {
                var thenB = NewCfgBlock();
                var join = NewCfgBlock();
                var elseB = i.Else is null ? join : NewCfgBlock();
                BranchTo(_cfgCur, i.Cond, thenB, elseB);
                _cfgCur = thenB; BuildCfg(i.Then); LinkGoto(_cfgCur, join);
                if (i.Else is { } els) { _cfgCur = elseB; BuildCfg(els); LinkGoto(_cfgCur, join); }
                _cfgCur = join;
                break;
            }

            case While w:
            {
                var head = NewCfgBlock();
                var bodyB = NewCfgBlock();
                var after = NewCfgBlock();
                LinkGoto(_cfgCur, head);
                BranchTo(head, w.Cond, bodyB, after);
                _cfgLoops.Add((after, head));         // break -> after, continue -> head
                _cfgCur = bodyB; BuildCfg(w.Body); LinkGoto(_cfgCur, head);
                _cfgLoops.RemoveAt(_cfgLoops.Count - 1);
                _cfgCur = after;
                break;
            }

            case DoWhile dw:
            {
                var bodyB = NewCfgBlock();
                var condB = NewCfgBlock();
                var after = NewCfgBlock();
                LinkGoto(_cfgCur, bodyB);
                _cfgLoops.Add((after, condB));        // continue -> the condition test
                _cfgCur = bodyB; BuildCfg(dw.Body); LinkGoto(_cfgCur, condB);
                _cfgLoops.RemoveAt(_cfgLoops.Count - 1);
                BranchTo(condB, dw.Cond, bodyB, after);
                _cfgCur = after;
                break;
            }

            case For f:
            {
                if (f.Init is { } fi) { BuildCfg(fi); }   // init is straight-line into _cfgCur
                var head = NewCfgBlock();
                var bodyB = NewCfgBlock();
                var postB = NewCfgBlock();
                var after = NewCfgBlock();
                LinkGoto(_cfgCur, head);
                if (f.Cond is { } c) { BranchTo(head, c, bodyB, after); } else { LinkGoto(head, bodyB); }
                _cfgLoops.Add((after, postB));        // continue -> the post step
                _cfgCur = bodyB; BuildCfg(f.Body); LinkGoto(_cfgCur, postB);
                _cfgLoops.RemoveAt(_cfgLoops.Count - 1);
                if (f.Post is { } post) { postB.Code.Add(new ExprStmt(post)); }
                LinkGoto(postB, head);
                _cfgCur = after;
                break;
            }

            case Break:
                if (_cfgLoops.Count == 0) { throw new IrUnsupportedException("`break` outside a loop or switch"); }
                LinkGoto(_cfgCur, _cfgLoops[^1].Brk);
                _cfgCur = NewCfgBlock();
                break;

            case Continue:
                if (_cfgLoops.Count == 0) { throw new IrUnsupportedException("`continue` outside a loop"); }
                LinkGoto(_cfgCur, _cfgLoops[^1].Cont);
                _cfgCur = NewCfgBlock();
                break;

            case Goto g:
                LinkGoto(_cfgCur, LabelBlock(g.Label));
                _cfgCur = NewCfgBlock();
                break;

            case Labeled lab:
            {
                var lb = LabelBlock(lab.Name);
                LinkGoto(_cfgCur, lb);                    // fall into the label
                _cfgCur = lb;
                BuildCfg(lab.Body);
                break;
            }

            case Return r:
                ReturnFrom(_cfgCur, r.Value);
                _cfgCur = NewCfgBlock();
                break;

            case Switch:
                throw new IrUnsupportedException("a switch inside a function that uses goto/labels is not yet supported on the wat target");

            case CaseLabelStmt:
                throw new IrUnsupportedException("a case/default label nested inside another statement (Duff's device) is not supported on the wat target");

            default:
                throw new IrUnsupportedException($"the wat target does not yet support the statement {s.GetType().Name} in a function that uses goto/labels");
        }
    }

    // ---- emission --------------------------------------------------------

    /// <summary>Lower a goto-using function: build its CFG, prune to the blocks
    /// reachable from the entry, then emit the dispatch loop.</summary>
    private void EmitViaCfg(FuncDef fn, CType ret)
    {
        _cfgAll = new List<CfgBlock>();
        _cfgLabels.Clear();
        _cfgLoops.Clear();
        var entry = NewCfgBlock();
        _cfgCur = entry;
        foreach (var s in fn.Body.Stmts) { BuildCfg(s); }
        // The builder's final block falls off the end of the function.

        // Reachability from the entry, assigning each live block a dense br_table index.
        var order = new List<CfgBlock>();
        var seen = new HashSet<CfgBlock>();
        var queue = new Queue<CfgBlock>();
        queue.Enqueue(entry); seen.Add(entry);
        while (queue.Count > 0)
        {
            var b = queue.Dequeue();
            b.Index = order.Count;
            order.Add(b);
            foreach (var succ in Successors(b))
            {
                if (seen.Add(succ)) { queue.Enqueue(succ); }
            }
        }
        var m = order.Count;

        _scratchLbl = true;
        Line($"i32.const {entry.Index}");
        Line("local.set $__lbl");
        Line("loop $__disp");
        _indent++;
        for (var i = m - 1; i >= 0; i--) { Line($"block $cfb{i}"); _indent++; }
        // Dispatch: jump to the block named by $__lbl. The default (>= m, never taken)
        // is harmlessly aimed at block 0.
        Line("local.get $__lbl");
        var table = string.Join(" ", Enumerable.Range(0, m).Select(i => $"$cfb{i}"));
        Line($"br_table {table} $cfb0");
        for (var i = 0; i < m; i++)
        {
            _indent--;
            Line("end"); // $cfb{i}
            var b = order[i];
            foreach (var st in b.Code) { EmitStmt(st); }
            EmitCfgTransfer(b, fn, ret);
        }
        _indent--;
        Line("end"); // $__disp
        Line("unreachable");
    }

    private static IEnumerable<CfgBlock> Successors(CfgBlock b) => b.Kind switch
    {
        CfgKind.Goto => new[] { b.T! },
        CfgKind.Branch => new[] { b.T!, b.F! },
        _ => System.Array.Empty<CfgBlock>(),
    };

    /// <summary>Emit a block's control transfer: a goto/branch sets <c>$__lbl</c> to the
    /// successor's index and re-enters the dispatch; a return leaves the function; a
    /// fall-off runs the function's implicit terminator.</summary>
    private void EmitCfgTransfer(CfgBlock b, FuncDef fn, CType ret)
    {
        switch (b.Kind)
        {
            case CfgKind.Goto:
                Line($"i32.const {b.T!.Index}");
                Line("local.set $__lbl");
                Line("br $__disp");
                break;

            case CfgKind.Branch:
                // $__lbl = cond ? trueIdx : falseIdx, then re-dispatch.
                Line($"i32.const {b.T!.Index}");
                Line($"i32.const {b.F!.Index}");
                EmitCond(b.Cond!);
                Line("select");
                Line("local.set $__lbl");
                Line("br $__disp");
                break;

            case CfgKind.Return:
                if (b.RetVal is { } v)
                {
                    EmitExpr(v);
                    EmitConvert(v.Type, _currentRet);
                }
                RestoreSp();
                Line("return");
                break;

            default: // FallOff — the natural end of the function
                EmitFnEnd(fn, ret);
                break;
        }
    }
}
