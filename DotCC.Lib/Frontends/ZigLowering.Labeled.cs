#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Labeled blocks and labeled loops: a labeled VALUE block (<c>blk: { …; break :blk v; }</c>) filled into
/// a temp, <c>break :label v</c>, and the statement forms of both. One concern of the <see cref="ZigLowering"/>
/// binder.</summary>
internal sealed partial class ZigLowering
{
    /// <summary>Lower a labeled block used as a VALUE — <c>blk: { …; break :blk v; }</c> — at a
    /// statement RHS position (Milestone L, part 2). A statement form can't be an expression, so we
    /// use the roadmap's temp-fill: a fresh result temp (<c>__blkN</c>) is declared, the block body
    /// is lowered with each <c>break :blk v</c> rewritten (in <see cref="LowerLabeledBreak"/>) to
    /// "assign the temp, <c>goto __blkN_end</c>", an end label follows the body, and <paramref
    /// name="consume"/> builds the surrounding statement that reads the temp (the decl / return /
    /// assignment). The temp's type is the <paramref name="sink"/> when known (an annotated decl, a
    /// function return, an lvalue), else the first <c>break</c> value's type. The temp is declared in
    /// the ENCLOSING scope (before the body's), so a <c>break</c> inside can assign it and the
    /// consumer outside can read it. The end label wraps an empty block (<c>__blkN_end: { }</c>) so a
    /// following declaration is legal — a C# label can't directly precede a declaration (CS1023).</summary>
    private CStmt LowerLabeledValueBlock(string label, Item blockItem, CType? sink, Func<Symbol, CStmt> consume)
        => LowerLabeledValueBody(label, () => LowerBlock(blockItem), sink, consume);
    /// <summary>True for a labeled value: a labeled block (<c>blk: { … }</c>) or a labeled switch
    /// (<c>sw: switch (x) { … break :sw v; … }</c>, std.math.shl).</summary>
    private static bool IsLabeledValue(Item item) => item.Content is Zig.LabeledBlock or Zig.LabeledSwitch;
    /// <summary><see cref="LowerLabeledValueBlock"/> for either labeled value form (<see cref="IsLabeledValue"/>): a
    /// labeled switch's body is the switch as a STATEMENT, each prong's <c>break :label v</c> filling the result temp.</summary>
    private CStmt LowerLabeledValue(Item labeled, CType? sink, Func<Symbol, CStmt> consume) => labeled.Content switch
    {
        Zig.LabeledBlock lb => LowerLabeledValueBlock(Tok(lb.Arg0), lb.Arg2, sink, consume),
        Zig.LabeledSwitch { Arg2.Content: Zig.SwitchExpr sw } ls => LowerLabeledValueBody(Tok(ls.Arg0), () => LowerContinuableSwitchBody(Tok(ls.Arg0), sw.Arg2, sw.Arg5), sink, consume),
        _ => throw new IrUnsupportedException("internal: not a labeled value: " + (labeled.Content?.GetType().Name ?? "null")),
    };
    /// <summary>A labeled switch's own body as a value (<see cref="LowerLabeledSwitchBody"/>), which a <c>continue
    /// :label operand</c> may run again (<see cref="LowerRedispatchingSwitch"/>).</summary>
    private CStmt LowerContinuableSwitchBody(string label, Item subjectItem, Item prongsItem)
    {
        _pendingSwitchContinueLabel = label;
        return LowerLabeledSwitchBody(label, subjectItem, prongsItem);
    }
    /// <summary>A labeled switch's body: the switch as a statement, its bare value prongs breaking to <paramref name="label"/>.</summary>
    private CStmt LowerLabeledSwitchBody(string label, Item subjectItem, Item prongsItem)
    {
        _pendingSwitchValueLabel = label;
        return LowerSwitchStmt(subjectItem, prongsItem);
    }
    /// <summary>A labeled value-block in a sub-expression (std.hash.XxHash3.final's `acc.digest(len, last_block: { … })`, a call
    /// argument, task #178): its statements run in the statement's hoist, before the expression, and fill a result temp the
    /// position reads. Refused past an earlier side-effecting operand of the same statement, whose order it would change.</summary>
    private CExpr HoistLabeledValue(Item labeled, CType? sink)
    {
        var buf = RequireHoistable("labeled value-block");
        Symbol? result = null;
        var stmt = LowerLabeledValue(labeled, sink, temp => { result = temp; return new Seq(new List<CStmt>()); });
        if (result is not { } resultTemp)
        {
            throw new IrUnsupportedException("internal: a labeled value-block produced no result temp");
        }
        buf.Add(stmt);
        return new VarRef(resultTemp) { Type = resultTemp.Type };
    }
    /// <summary>The core of <see cref="LowerLabeledValueBlock"/>: <paramref name="lowerBody"/> lowers the body while the
    /// label's break target is pushed.</summary>
    private CStmt LowerLabeledValueBody(string label, Func<CStmt> lowerBody, CType? sink, Func<Symbol, CStmt> consume)
    {
        var n = _blockLabelCounter++;
        var endLabel = "__blk" + n + "_end";
        // Declared with a provisional type; retyped below once the result type is resolved. The
        // counter-unique name never collides, so declaring it up front is safe.
        var temp = _symbols.Declare(new Symbol { Name = "__blk" + n, Kind = SymKind.Var, Type = sink ?? CType.Int });
        var target = new LabeledBlockTarget { Label = label, Temp = temp, EndLabel = endLabel, Sink = sink, ResultType = sink };
        _labeledBlocks.Push(target);
        var body = lowerBody();   // each `break :label v` reads `target` via LowerLabeledBreak
        _labeledBlocks.Pop();
        var resultType = target.ResultType
            ?? throw new IrUnsupportedException(
                $"labeled block ':{label}' must yield a value via `break :{label} <value>;`");
        temp.Type = resultType;
        var stmts = new List<CStmt>
        {
            // `T __blkN = default;` — default-initialized so C# definite-assignment is satisfied even
            // though every real path assigns via a `break` (the gotos defeat flow analysis).
            new DeclStmt(new List<LocalDecl> { new(temp, new DefaultLit { Type = resultType }) }),
            body,
            new Labeled(endLabel, new Block(new List<CStmt>())),
            consume(temp),
        };
        return new Seq(stmts);
    }
    /// <summary>Lower an unlabeled <c>break v;</c> (Milestone Y, part 2) — yield <paramref
    /// name="valueItem"/> from the INNERMOST value-position loop on the stack.</summary>
    private CStmt LowerBreakValue(Item valueItem)
    {
        // `break sort.insertionContext(a, b, context);` in a plain `while (true)` (std.sort.pdq): a VOID break value is the
        // loop's own (void) result, so the call runs and the loop ends.
        if (_loopValues.Count == 0 && valueItem.Content is Zig.CallArgs or Zig.CallNoArgs
            && LowerExpr(valueItem) is { Type.Unqualified: CType.VoidType } voidCall)
        {
            return new Block(new List<CStmt> { new ExprStmt(voidCall), LowerUnlabeledBreak() });
        }
        if (_loopValues.Count == 0)
        {
            throw new IrUnsupportedException(
                "`break <value>;` is only valid inside a value-position `while`/`for … else` loop"
                + (_currentFnName.Length > 0 ? $" (in '{_currentFnName}')" : ""));
        }
        return BuildLoopBreakValue(_loopValues.Peek(), valueItem);
    }
    /// <summary>Lower <c>break :label v;</c> (Milestone L, part 2; extended in Milestone Y, part 2) —
    /// yield <paramref name="valueItem"/> from the enclosing construct named <paramref name="label"/>:
    /// a labeled value-position loop (<c>lbl: while/for … else</c>) or a labeled value-block. Assigns
    /// that construct's result temp, then <c>goto</c> its end label. Resolves innermost-first; the
    /// value is sink-typed to the result type when known, and the first such break fixes that type.</summary>
    private CStmt LowerLabeledBreak(string label, Item valueItem)
    {
        // `break :blk switch (d.digits[0]) { 5...9 => break, 0, 1 => 2, else => 1 }` (std.fmt.parse_float's convertSlow): a
        // switch whose prongs are not all values (a jump out of the loop here) cannot be a C# switch expression, so it is
        // the switch as a statement, its value prongs breaking to the same label, as a labeled switch's do.
        if (valueItem.Content is Zig.SwitchExpr bs && Flatten(bs.Arg5).Any(p => p.Content is not Zig.ProngExpr))
        {
            return LowerLabeledSwitchBody(label, bs.Arg2, bs.Arg5);
        }
        // A labeled value-position loop (`lbl: while/for … else`, Milestone Y part 2) — innermost-first.
        foreach (var lv in _loopValues)
        {
            if (lv.Label == label) { return BuildLoopBreakValue(lv, valueItem); }
        }
        // A value break targeting a labeled STATEMENT loop (no `else` → not a value loop) is still a
        // clear deferred error — and is invalid Zig anyway (a value `break` needs a value loop).
        if (_labeledBlocks.All(t => t.Label != label) && _labeledLoops.FirstOrDefault(l => l.Label == label) is { IsBlock: true })
        {
            throw new IrUnsupportedException(
                $"`break :{label} <value>` yields a value, but ':{label}' is a block statement, whose value is void");
        }
        if (_labeledBlocks.All(t => t.Label != label) && _labeledLoops.Any(l => l.Label == label))
        {
            throw new IrUnsupportedException(
                $"`break :{label} <value>` yields a value from a labeled loop, but ':{label}' is a statement loop " +
                "with no `else` clause — give it an `else` to make it a value loop");
        }
        var target = _labeledBlocks.FirstOrDefault(t => t.Label == label)
            ?? throw new IrUnsupportedException(
                $"`break :{label}` has no enclosing labeled block ':{label}'");
        var value = target.Sink is { } sk ? LowerExprSink(valueItem, sk) : LowerExpr(valueItem);
        target.ResultType ??= value.Type;
        var tref = new VarRef(target.Temp) { Type = target.ResultType, IsLValue = true };
        // A `Block` (not a brace-less `Seq`): this pair is a single statement, and as an `if`/`while`
        // body the backend braces a Block but renders a Seq brace-less — which would leave the `goto`
        // unconditional (`if (c) temp = v; goto end;`). A `goto` out of the block to the enclosing
        // end label is legal C#; the assign-and-goto declare nothing, so the extra scope is harmless.
        return new Block(new List<CStmt>
        {
            new ExprStmt(new Assign(null, tref, value) { Type = target.ResultType }),
            new Goto(target.EndLabel),
        });
    }
    /// <summary>Lower a labeled loop <c>lbl: while/for (…) { … }</c> (Milestone L, part 3). The loop
    /// itself lowers normally (its record name is unchanged by the grammar's <c>LoopStmt</c> factor);
    /// while its body is lowered, an enclosing <see cref="LabeledLoopTarget"/> lets a <c>break :lbl</c>
    /// / <c>continue :lbl</c> within (possibly inside a nested loop) resolve to a <c>goto</c>. After
    /// lowering, the continue label is appended to the END of the loop body (so a <c>goto</c> there
    /// falls into the loop's natural iteration step) and the break label is placed just AFTER the loop
    /// — each only when actually referenced, to avoid a C# unreferenced-label warning.</summary>
    private CStmt LowerLabeledLoop(string label, Item loopItem)
    {
        var n = _loopLabelCounter++;
        var t = new LabeledLoopTarget
        {
            Label = label, BreakLabel = "__loop" + n + "_brk", ContLabel = "__loop" + n + "_cont",
        };
        _labeledLoops.Push(t);
        // The loop is its own unlabeled break target too (a `break` in a `switch` in its body), sharing
        // the break label, so LowerStmt must not wrap it again: the continue label is appended to the
        // loop's own body below, which a wrapping Seq would hide.
        var unlabeled = new LoopBreakTarget { BreakLabel = t.BreakLabel };
        _loopBreakTargets.Push(unlabeled);
        _loopBeingWrapped = loopItem;
        CStmt loop;
        try { loop = LowerStmt(loopItem); }   // a While / For / DoWhile; break/continue :lbl read `t`
        finally
        {
            _loopBreakTargets.Pop();
            _labeledLoops.Pop();
        }
        if (unlabeled.Used) { t.BreakUsed = true; }
        if (t.ContUsed) { loop = WithLoopBody(loop, body => AppendLabel(body, t.ContLabel)); }
        var stmts = new List<CStmt> { loop };
        if (t.BreakUsed) { stmts.Add(new Labeled(t.BreakLabel, new Block(new List<CStmt>()))); }
        return new Seq(stmts);
    }
    /// <summary>Lower a labeled block STATEMENT, <c>lbl: { … }</c> or <c>lbl: switch (x) { … }</c> (task #130,
    /// std.bit_set.DynamicBitSetUnmanaged.resize's <c>realloc: { … break :realloc; … }</c>). Its value is void, so the
    /// only jump out is <c>break :lbl;</c>, a <c>goto</c> to an end label placed after the body when used. It reuses
    /// <see cref="LabeledLoopTarget"/> marked <see cref="LabeledLoopTarget.IsBlock"/>, so a <c>continue :lbl</c> is
    /// refused as zig refuses it, and an unlabeled <c>break</c> / <c>continue</c> inside still reaches the enclosing
    /// loop (a block is not a loop).</summary>
    private CStmt LowerLabeledBlockStmt(Item labeled)
    {
        var (label, lowerBody) = labeled.Content switch
        {
            Zig.LabeledBlock lb => (Tok(lb.Arg0), (Func<CStmt>)(() => LowerBlock(lb.Arg2))),
            Zig.LabeledSwitch { Arg2.Content: Zig.SwitchExpr sw } ls => (Tok(ls.Arg0), () =>
            {
                _pendingSwitchContinueLabel = Tok(ls.Arg0);
                return LowerSwitchStmt(sw.Arg2, sw.Arg5);
            }),
            _ => throw new IrUnsupportedException("internal: not a labeled block: " + (labeled.Content?.GetType().Name ?? "null")),
        };
        var n = _loopLabelCounter++;
        var t = new LabeledLoopTarget
        {
            Label = label, BreakLabel = "__blk" + n + "_brk", ContLabel = "__blk" + n + "_cont", IsBlock = true,
        };
        _labeledLoops.Push(t);
        CStmt body;
        try { body = lowerBody(); }
        finally { _labeledLoops.Pop(); }
        return t.BreakUsed
            ? new Seq(new List<CStmt> { body, new Labeled(t.BreakLabel, new Block(new List<CStmt>())) })
            : body;
    }

    /// <summary>The label of the labeled switch about to be lowered (statement or value form), set just before its
    /// <see cref="LowerSwitchStmt"/> and taken by it, so no switch nested in a prong inherits it (GH #286).</summary>
    private string? _pendingSwitchContinueLabel;

    /// <summary>The runtime labeled switches whose prongs are being lowered, innermost on top: what a <c>continue
    /// :label operand</c> runs again (<see cref="LowerSwitchContinue"/>).</summary>
    private readonly Stack<SwitchContinueTarget> _switchContinueTargets = new();

    /// <summary>The counter that names a labeled switch's operand temp (<c>__lsw&lt;n&gt;</c>), apart from the block
    /// counter so other temps keep their numbers.</summary>
    private int _switchContinueCounter;

    /// <summary>A runtime labeled switch: its label, the temp holding its operand, the label before the switch a
    /// <c>continue</c> jumps back to, and whether one did.</summary>
    private sealed class SwitchContinueTarget(string label, Symbol temp, string topLabel)
    {
        public string Label { get; } = label;
        public Symbol Temp { get; } = temp;
        public string TopLabel { get; } = topLabel;
        public bool Used { get; set; }
    }

    /// <summary>A labeled switch over a runtime operand (GH #286, zig 0.14's state-machine idiom: <c>state: switch
    /// (State.start) { .start =&gt; continue :state .identifier, … }</c>). The operand goes to a temp the switch
    /// reads, and <c>continue :state x</c> assigns the temp and jumps back to a label before the switch, which runs
    /// again on the new operand. The label is placed only when a continue used it; the temp is there regardless,
    /// since the prongs read it before that is known, which costs a copy of the operand. A tagged-union operand is
    /// not copied (a pointer capture points into the operand itself), so it takes no continue yet.</summary>
    private CStmt LowerRedispatchingSwitch(string label, CExpr subject, Func<CExpr, CStmt> lowerSwitch)
    {
        var n = _switchContinueCounter++;
        var temp = _symbols.Declare(new Symbol { Name = "__lsw" + n, Kind = SymKind.Var, Type = subject.Type });
        var target = new SwitchContinueTarget(label, temp, "__lsw" + n + "_top");
        _switchContinueTargets.Push(target);
        CStmt body;
        try { body = lowerSwitch(new VarRef(temp) { Type = temp.Type, IsLValue = true }); }
        finally { _switchContinueTargets.Pop(); }
        return new Seq(new List<CStmt>
        {
            new DeclStmt(new List<LocalDecl> { new(temp, subject) }),
            target.Used ? new Labeled(target.TopLabel, body) : body,
        });
    }

    /// <summary>Lower <c>continue :label operand</c> (GH #286): the operand, at the switch operand's type, goes to the
    /// labeled switch's temp, and the switch runs again (<see cref="LowerRedispatchingSwitch"/>). Innermost first.</summary>
    private CStmt LowerSwitchContinue(string label, Item operandItem)
    {
        var target = _switchContinueTargets.FirstOrDefault(t => t.Label == label)
            ?? throw new IrUnsupportedException(
                $"`continue :{label} <operand>`: ':{label}' is not a labeled switch over a runtime operand that dotcc runs "
                + "again (a loop's continue takes no operand; a switch over a tagged union, or one selected at compile time, "
                + "takes no continue yet)");
        return Hoisted(() =>
        {
            var operand = LowerExprSink(operandItem, target.Temp.Type);
            target.Used = true;
            var tref = new VarRef(target.Temp) { Type = target.Temp.Type, IsLValue = true };
            return new Block(new List<CStmt>
            {
                new ExprStmt(new Assign(null, tref, operand) { Type = target.Temp.Type }),
                new Goto(target.TopLabel),
            });
        });
    }
}
