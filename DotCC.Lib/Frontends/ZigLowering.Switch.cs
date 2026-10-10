#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Switches: prongs (values, ranges, <c>inline</c> prongs, captures), a tagged-union switch, a switch
/// statement and a switch expression, and a comptime integer switch folded to its taken prong. One concern of the
/// <see cref="ZigLowering"/> binder.</summary>
internal sealed partial class ZigLowering
{
    /// <summary>Lower a statement switch's bare-expression prong body. A nested <c>switch</c> there is itself a
    /// STATEMENT (std.math.sqrt's <c>.int =&gt; |I| switch (I.signedness) { .unsigned =&gt; return …, … }</c>), so
    /// its prongs may return or raise, which a switch EXPRESSION's may not; anything else is an expression
    /// statement.</summary>
    private CStmt LowerProngExprStmt(Item e) => e.Content switch
    {
        // In a LABELED switch (`r: switch (x) { 0 => 10, … }`), a bare value prong is the switch's value: `break :r 10`.
        // (`unreachable` stays the trap it is.) Each is a statement of its own prong, so what its value hoists
        // (`0 => a orelse return 9`, `… else continue :round .away`) runs in that prong, not ahead of the whole switch,
        // where the statement holding a labeled value switch had taken it (GH #286).
        _ when _activeSwitchValueLabel is { } valueLabel && !IsUnreachableItem(e) => Hoisted(() => LowerLabeledBreak(valueLabel, e)),
        // `=> if (c) x,` / `=> if (c) { … } else { … },` (zig-grammar-peg P3a): an `if` whose arms are statements.
        _ when IsStatementIf(e) => LowerStatementIf(e),
        Zig.SwitchExpr s => LowerSwitchStmt(s.Arg2, s.Arg5),
        _ => Hoisted(() => new ExprStmt(LowerExpr(e))),
    };
    /// <summary>The label a labeled switch hands to its OWN switch statement (<see cref="LowerLabeledValue"/>), taken
    /// by that switch as it starts, so no switch nested in one of its prongs inherits it.</summary>
    private string? _pendingSwitchValueLabel;
    /// <summary>The label of the labeled switch whose prongs are being lowered (null inside any other switch):
    /// its bare value prongs break to it (<see cref="LowerProngExprStmt"/>).</summary>
    private string? _activeSwitchValueLabel;
    /// <summary>True when <paramref name="e"/> is the bare <c>unreachable</c>.</summary>
    private static bool IsUnreachableItem(Item e) => e.Content is Zig.Ident { Arg0: var tok } && Tok(tok) == "unreachable";
    /// <summary>The prong a switch over a comptime-known integer subject takes (case values, ranges, then
    /// <c>else</c>), or null when the subject does not fold or the prong captures.</summary>
    private ZigProng? TrySelectConstProng(Item subjectItem, Item prongsItem)
    {
        CExpr subject;
        // A subject with no value lowering (`@typeInfo(T)`, a comptime tag) is the comptime-tag path's, not this one.
        try
        {
            using (EnterThrowawayHoist()) { subject = LowerExpr(subjectItem); }
        }
        catch (IrUnsupportedException)
        {
            return null;
        }
        if (_ir.ConstEval(subject) is not { } v) { return null; }
        ZigProng? elseProng = null;
        foreach (var prongItem in Flatten(prongsItem))
        {
            var prong = DecomposeProng(prongItem);
            if (prong.CaptureName is { } cap && cap != "_") { return null; }
            if (prong.CaseVals.Content is Zig.CaseElse) { elseProng = prong; continue; }
            foreach (var label in LowerCaseVals(prong.CaseVals, subject.Type))
            {
                if (label.CaseExpr is not { } ce || _ir.ConstEval(ce) is not { } lo) { return null; }
                var hi = label.HiExpr is { } he ? _ir.ConstEval(he) : lo;
                if (hi is null) { return null; }
                if (v >= lo && v <= hi) { return prong; }
            }
        }
        return elseProng;
    }
    /// <summary>Lower the one prong a comptime subject selected (a block, a value, a <c>return</c>, a jump, an
    /// assignment), as a statement.</summary>
    private CStmt LowerSelectedProng(ZigProng prong) => prong switch
    {
        { Block: { } blk } => LowerBlock(blk),
        { Expr: { } e } => LowerProngExprStmt(e),
        { Return: { } r } => Hoisted(() => LowerReturn(r)),
        { ReturnsVoid: true } => LowerReturnVoid(),
        { Jump: { } j } => LowerProngJump(j),
        { Assign: { } pa } => LowerProngAssign(pa),
        { IfCaptureReturn: { } icr } => LowerIfCapture(icr.Arg4, Tok(icr.Arg7), icr.Arg9, null, null),
        { Loop: { } loop } => LowerStmt(loop),
        _ => new Seq(new List<CStmt>()),
    };
    /// <summary>True for an <c>if</c> expression whose arms are statements rather than values (zig-grammar-peg P3a): an
    /// else-less one (zig's <c>else</c> is optional), or one with a non-empty <c>{ … }</c> block arm. Its value is void, so
    /// it has a meaning only where a statement stands (an expression statement, a prong body).</summary>
    private static bool IsStatementIf(Item e) => e.Content is Zig.IfExprNoElse or Zig.IfExprCaptureNoElse
        || e.Content is Zig.IfExpr ie && (ie.Arg4.Content is Zig.Block || ie.Arg6.Content is Zig.Block);

    /// <summary>Lower an <see cref="IsStatementIf">statement <c>if</c></see>. A condition that folds at compile time keeps
    /// only the taken arm, so an untaken <c>@compileError</c> / switch / block is never analysed (what the retired
    /// <c>=&gt; if (c) …</c> prong forms did); otherwise a runtime <c>if</c>.</summary>
    private CStmt LowerStatementIf(Item e)
    {
        switch (e.Content)
        {
            case Zig.IfExprNoElse n:
                if ((TryFoldComptimeCondition(n.Arg2) ?? TryFoldTypeIfCondition(n.Arg2)) is { } takenThen)
                {
                    return takenThen ? LowerArmStmt(n.Arg4) : new Seq(new List<CStmt>());
                }
                return new If(LowerExpr(n.Arg2), LowerArmStmt(n.Arg4), null);
            case Zig.IfExprCaptureNoElse c:
                return LowerIfCapture(c.Arg2, Tok(c.Arg5), c.Arg7, null, null);
            case Zig.IfExpr ie:
                if ((TryFoldComptimeCondition(ie.Arg2) ?? TryFoldTypeIfCondition(ie.Arg2)) is { } taken)
                {
                    return LowerArmStmt(taken ? ie.Arg4 : ie.Arg6);
                }
                return new If(LowerExpr(ie.Arg2), LowerArmStmt(ie.Arg4), LowerArmStmt(ie.Arg6));
            default:
                throw new IrUnsupportedException("internal: statement if " + (e.Content?.GetType().Name ?? "null"));
        }
    }

    /// <summary>One arm of a <see cref="LowerStatementIf">statement <c>if</c></see>: a block, a jump, a nested statement
    /// <c>if</c>, a <c>switch</c> statement, or an expression statement.</summary>
    private CStmt LowerArmStmt(Item arm) => arm.Content switch
    {
        Zig.Block => LowerBlock(arm),
        Zig.VoidValue => new Seq(new List<CStmt>()),   // an empty `{}` arm
        _ when IsNoreturnArm(arm) => LowerExitArm(arm),
        _ when IsStatementIf(arm) => LowerStatementIf(arm),
        Zig.SwitchExpr s => LowerSwitchStmt(s.Arg2, s.Arg5),
        _ => Hoisted(() => new ExprStmt(LowerExpr(arm))),
    };
    /// <summary>A copy of an unrolled body with its TRAILING jump removed, and which jump it was: a <c>break</c>
    /// (a plain one, or the goto the inline loop's break target lowers to under a switch), a
    /// <c>continue</c>, or none. Only the last statement is inspected, through nested blocks.</summary>
    private static (CStmt Body, CStmt? Jump) TrimTrailingJump(CStmt s, string breakLabel)
    {
        switch (s)
        {
            case Break or Continue:
                return (new Seq(new List<CStmt>()), s);
            case Goto g when g.Label == breakLabel:
                return (new Seq(new List<CStmt>()), new Break());
            case Block { Stmts.Count: > 0 } b:
            {
                var (last, jump) = TrimTrailingJump(b.Stmts[^1], breakLabel);
                if (jump is null) { return (s, null); }
                var stmts = new List<CStmt>(b.Stmts.Take(b.Stmts.Count - 1)) { last };
                return (new Block(stmts), jump);
            }
            case Seq { Stmts.Count: > 0 } q:
            {
                var (last, jump) = TrimTrailingJump(q.Stmts[^1], breakLabel);
                if (jump is null) { return (s, null); }
                var stmts = new List<CStmt>(q.Stmts.Take(q.Stmts.Count - 1)) { last };
                return (new Seq(stmts), jump);
            }
            default:
                return (s, null);
        }
    }
    /// <summary>True when a statement contains a <c>goto</c> to <paramref name="label"/> anywhere.</summary>
    private static bool ContainsGotoTo(CStmt s, string label) => s switch
    {
        Goto g => g.Label == label,
        Block b => b.Stmts.Any(x => ContainsGotoTo(x, label)),
        Seq q => q.Stmts.Any(x => ContainsGotoTo(x, label)),
        If i => ContainsGotoTo(i.Then, label) || (i.Else is { } e && ContainsGotoTo(e, label)),
        Labeled l => ContainsGotoTo(l.Body, label),
        _ => false,
    };
    /// <summary>An assignment prong body: <c>v =&gt; lhs = rhs</c>, or a compound one (<c>0 =&gt; hits += 1</c>).</summary>
    private CStmt LowerProngAssign(Zig.ProngAssign pa) => LowerAssignProngBody(pa.Arg2, pa.Arg3, pa.Arg4);
    /// <summary>The assignment an assignment prong performs (<c>lhs = rhs</c>, or a compound <c>lhs += rhs</c>), shared by
    /// the plain form and its capture twin (<c>.on =&gt; |v| total += v</c>, task #109). A saturating operator had read
    /// as a plain <c>=</c> here (<see cref="CompoundOpOf"/> has none), so <c>x +|= y</c> in a prong stored <c>y</c>.</summary>
    private CStmt LowerAssignProngBody(Item lhs, Item opItem, Item rhs) => LowerAssignOpStmt(lhs, opItem, rhs);
    /// <summary>The binary operator of a compound continue-expression assignment (<c>i += 1</c>), or null
    /// for a plain <c>=</c>.</summary>
    private static BinOp? CompoundOpOf(Item opItem) => opItem.Content switch
    {
        Zig.AopAdd or Zig.AopAddWrap => BinOp.Add,
        Zig.AopSub or Zig.AopSubWrap => BinOp.Sub,
        Zig.AopMul or Zig.AopMulWrap => BinOp.Mul,
        Zig.AopDiv => BinOp.Div,
        Zig.AopMod => BinOp.Mod,
        Zig.AopShl => BinOp.Shl,
        Zig.AopShr => BinOp.Shr,
        Zig.AopBitAnd => BinOp.BitAnd,
        Zig.AopBitOr => BinOp.BitOr,
        Zig.AopBitXor => BinOp.BitXor,
        _ => null,
    };
    /// <summary>Lower a <c>switch</c> statement, counting it as a barrier for an unlabeled <c>break</c>
    /// in its prongs (<see cref="LoopBreakTarget"/>).</summary>
    private CStmt LowerSwitchStmt(Item subjectItem, Item prongsItem)
    {
        var previousLabel = _activeSwitchValueLabel;
        _activeSwitchValueLabel = _pendingSwitchValueLabel;   // this switch's own label, or null for any other switch
        _pendingSwitchValueLabel = null;
        var continueLabel = _pendingSwitchContinueLabel;      // a labeled switch's label, which a continue may name
        _pendingSwitchContinueLabel = null;
        try { return WithSwitchBarrier(() => LowerSwitchStmtCore(subjectItem, prongsItem, continueLabel)); }
        finally { _activeSwitchValueLabel = previousLabel; }
    }
    /// <summary>Run <paramref name="lowerSwitch"/>, which builds a C# <c>switch</c>, as a barrier for an
    /// unlabeled <c>break</c> in its prongs (<see cref="LoopBreakTarget"/>).</summary>
    private CStmt WithSwitchBarrier(Func<CStmt> lowerSwitch)
    {
        var loop = _loopBreakTargets.Count > 0 ? _loopBreakTargets.Peek() : null;
        if (loop is not null) { loop.SwitchDepth++; }
        try { return lowerSwitch(); }
        finally { if (loop is not null) { loop.SwitchDepth--; } }
    }
    /// <summary>Lower a JUMP prong body (<c>=&gt; break</c>, <c>=&gt; continue :outer</c>,
    /// <c>=&gt; break :blk v</c>) exactly as the matching statement lowers.</summary>
    private CStmt LowerProngJump(Item jump) => jump.Content switch
    {
        Zig.PjBreak => LowerUnlabeledBreak(),
        Zig.PjBreakLabel b => LowerLabeledLoopJump(Tok(b.Arg2), isContinue: false),
        Zig.PjBreakLabelValue b => Hoisted(() => LowerLabeledBreak(Tok(b.Arg2), b.Arg3)),
        Zig.PjContinue => new Continue(),
        Zig.PjContinueLabel c => LowerLabeledLoopJump(Tok(c.Arg2), isContinue: true),
        Zig.PjContinueLabelValue c => LowerSwitchContinue(Tok(c.Arg2), c.Arg3),
        _ => throw new IrUnsupportedException("zig switch jump prong: " + (jump.Content?.GetType().Name ?? "null")),
    };
    /// <summary>The body of <see cref="LowerSwitchStmt"/>. <paramref name="continueLabel"/> is the switch's label when it
    /// is a labeled switch, whose prongs a <c>continue :label operand</c> may run again (<see cref="LowerRedispatchingSwitch"/>);
    /// a compile-time-selected prong is lowered alone, as before.</summary>
    private CStmt LowerSwitchStmtCore(Item subjectItem, Item prongsItem, string? continueLabel = null)
    {
        // A switch over a comptime-known VALUE selects its prong now, as zig does. While an `inline` loop
        // unrolls (`switch (fmt[i])` in std.Io.Writer.print) the loop's comptime control (a `break` in the
        // taken prong) must be known to know when to stop unrolling; and in a generic instance (the scope
        // LowerIfStmt folds a constant condition in, too) an unselected prong is never analysed
        // (std.Io.Writer.printValue's `switch (fmt.len)` over a comptime format string, whose other prongs
        // call `invalidFmtError`, a `@compileError`).
        if ((_inlineUnrollDepth > 0 || _inGenericInstance) && TrySelectConstProng(subjectItem, prongsItem) is { } constProng)
        {
            return LowerSelectedProng(constProng);
        }
        // A COMPTIME subject — `switch (@typeInfo(T))` / `switch (info.signedness)` — selects its
        // prong at lowering time and lowers ONLY that one (road-to-zig-std S5).
        if (SelectComptimeProng(subjectItem, prongsItem, out var ctPayload) is { } ctProng)
        {
            EnterComptimeProng(ctProng, ctPayload);
            try
            {
                return LowerSelectedProng(ctProng);
            }
            finally
            {
                ExitComptimeProng();
            }
        }
        var subject = LowerExpr(subjectItem);
        var u = subject.Type.Unqualified;
        var uname = u switch
        {
            CType.Named n => n.Name,
            CType.Pointer { Pointee: var pe } when pe.Unqualified is CType.Named pn => pn.Name,
            _ => null,
        };
        if (uname is not null && _unions.TryGetValue(uname, out var info))
        {
            return LowerUnionSwitch(subject, prongsItem, info);
        }
        if (continueLabel is not null)
        {
            return LowerRedispatchingSwitch(continueLabel, subject, s => LowerSwitch(s, prongsItem));
        }
        return LowerSwitch(subject, prongsItem);
    }
    /// <summary>Lower a non-union <c>switch (subject) { prong, … }</c> to the C IR
    /// <see cref="Switch"/>. Each prong (<c>CaseVals =&gt; Block</c>) becomes a
    /// <see cref="SwitchSection"/>: its case values are the labels (<c>else</c> → the null
    /// default label), and its braced block is the body. Zig switch has NO fall-through, so a
    /// terminating <see cref="Break"/> is appended to any section that doesn't already end
    /// control flow — otherwise the C# backend would synthesize C's fall-through jump. A capture <c>|x|</c> binds a
    /// payload only on a tagged-union switch, and on an ERROR switch the error itself (below); anything else rejects it,
    /// as zig does.</summary>
    private CStmt LowerSwitch(CExpr subject, Item prongsItem)
    {
        var sections = new List<SwitchSection>();
        var prongs = Flatten(prongsItem);
        // A switch over an error value binds a prong capture to that error (`error.ReadFailed, error.EndOfStream => |e|
        // return e`, `else => |e| return e` all through std.Io.Reader, task #202): dotcc erases error sets, so the narrowed
        // error zig binds is the subject's code. The subject is read once into a temp when a capture reads it again.
        var errorType = subject.Type is { Unqualified: CType.ErrorSetType } et ? et : null;
        var errorSwitch = errorType is not null;
        var pre = new List<CStmt>();
        if (errorType is { } swType && subject is not VarRef
            && prongs.Any(p => p.Content is Zig.ProngCapture or Zig.ProngCaptureExpr or Zig.ProngCaptureReturn
                                   or Zig.ProngCaptureReturnVoid or Zig.ProngCaptureJump))
        {
            var st = _symbols.Declare(new Symbol { Name = "__sw" + _blockLabelCounter++, Kind = SymKind.Var, Type = swType });
            pre.Add(new DeclStmt(new List<LocalDecl> { new(st, subject) }));
            subject = new VarRef(st) { Type = swType, IsLValue = true };
        }
        foreach (var prongItem in prongs)
        {
            if (errorSwitch && prongItem.Content is Zig.ProngCapture or Zig.ProngCaptureExpr or Zig.ProngCaptureReturn
                                                    or Zig.ProngCaptureReturnVoid or Zig.ProngCaptureJump)
            {
                sections.Add(LowerErrorCaptureSection(prongItem, subject));
                continue;
            }
            // `inline 0, 1, 2, 3 => |count| { … }` (std.hash.XxHash32's finalize, task #87): one section per case value, the
            // capture a comptime constant of that value (so the body's `inline for (0..count)` unrolls).
            if (prongItem.Content is Zig.InlineProng inlineProng)
            {
                sections.AddRange(LowerInlineProngSections(inlineProng.Arg1, subject));
                continue;
            }
            if (prongItem.Content is Zig.ProngCapture or Zig.ProngCaptureRef
                or Zig.ProngCaptureExpr or Zig.ProngCaptureReturn or Zig.ProngCaptureReturnVoid
                or Zig.ProngCaptureRefExpr or Zig.ProngCaptureRefReturn or Zig.ProngCaptureRefReturnVoid
                or Zig.ProngCaptureJump or Zig.ProngCaptureAssign)
            {
                throw new IrUnsupportedException(
                    "zig switch payload capture `|x|` is only valid on a tagged-union switch");
            }
            // A prong body is a braced Block (`=> { … }`), a bare expression (`=> expr`, an expression
            // statement here, e.g. `1 => doThing()`), or a `return` (`=> return [e]`) — the last is the
            // common shape for an enum switch whose arms each return (e.g. `switch (order) { .lt =>
            // return .lt, … }`). `return` bodies reuse the statement return-lowering (error-union
            // wrapping / errdefer routing), so an `E!T` prong return propagates correctly.
            Item caseVals;
            List<CStmt> body;
            switch (prongItem.Content)
            {
                case Zig.Prong p:            caseVals = p.Arg0;  body = new List<CStmt> { LowerBlock(p.Arg2) }; break;
                case Zig.ProngExpr pe:       caseVals = pe.Arg0; body = new List<CStmt> { LowerProngExprStmt(pe.Arg2) }; break;
                case Zig.ProngReturn pr:     caseVals = pr.Arg0; body = new List<CStmt> { Hoisted(() => LowerReturn(pr.Arg3)) }; break;
                case Zig.ProngReturnVoid pr: caseVals = pr.Arg0; body = new List<CStmt> { LowerReturnVoid() }; break;
                case Zig.ProngJump pj:       caseVals = pj.Arg0; body = new List<CStmt> { LowerProngJump(pj.Arg2) }; break;
                case Zig.ProngAssign pa:     caseVals = pa.Arg0; body = new List<CStmt> { LowerProngAssign(pa) }; break;
                case Zig.ProngLoop plp:      caseVals = plp.Arg0; body = new List<CStmt> { LowerStmt(plp.Arg2) }; break;
                case Zig.ProngIfCaptureReturn picr:
                    caseVals = picr.Arg0; body = new List<CStmt> { LowerIfCapture(picr.Arg4, Tok(picr.Arg7), picr.Arg9, null, null) }; break;
                // A runtime switch cannot run a `comptime { … }` arm; zig requires a comptime subject for one.
                case Zig.ProngComptimeBlock:
                    throw new IrUnsupportedException("zig `=> comptime { … }` prong in a switch whose subject is not comptime-known");
                default: throw new IrUnsupportedException("zig switch prong: " + (prongItem.Content?.GetType().Name ?? "null"));
            }
            var labels = LowerCaseVals(caseVals, subject.Type); // case values compare against the subject
            if (!EndsInJump(body)) { body.Add(new Break()); }   // no Zig fall-through
            sections.Add(new SwitchSection(labels, body));
        }
        // A switch over an enum with no `else` names every member (zig checks it), so no value reaches past it: an
        // unreachable default says so to C#, which otherwise sees a function whose every prong returns as falling off
        // its end (std.array_hash_map's capacityIndexSize, CS0161, task #135). An error switch with no `else` names every
        // error of its set the same way (std.Io.Reader.peekDelimiterInclusive's `} else |err| switch (err) { … }`, task
        // #202).
        if (subject.Type?.Unqualified is CType.Enum or CType.ErrorSetType
            && sections.All(s => s.Labels.All(l => l.CaseExpr is not null)))
        {
            sections.Add(new SwitchSection(new List<SwitchLabel> { new SwitchLabel(null) }, new List<CStmt>
            {
                new ExprStmt(new Call("__dotcc_unreachable", new List<CExpr>(), new List<CType>(), null) { Type = CType.Void }),
            }));
        }
        if (pre.Count == 0) { return new Switch(subject, sections); }
        pre.Add(new Switch(subject, sections));
        return new Seq(pre);
    }

    /// <summary>One section of an error switch whose prong captures the error (<c>… => |e| return e</c>, task #202): the
    /// capture is bound to the subject's code in the section's own scope, then the prong body runs.</summary>
    private SwitchSection LowerErrorCaptureSection(Item prongItem, CExpr subject)
    {
        var prong = DecomposeProng(prongItem);
        var labels = LowerCaseVals(prong.CaseVals, subject.Type);
        var stmts = new List<CStmt>();
        _symbols.EnterScope();
        try
        {
            if (prong.CaptureName is { } cap && cap != "_")
            {
                var capSym = _symbols.Declare(new Symbol { Name = cap, Kind = SymKind.Var, Type = subject.Type });
                stmts.Add(new DeclStmt(new List<LocalDecl> { new(capSym, subject) }));
            }
            if (prong.Block is { } block) { stmts.Add(LowerBlock(block)); }
            else if (prong.Expr is { } expr) { stmts.Add(LowerProngExprStmt(expr)); }
            else if (prong.Return is { } returned) { stmts.Add(Hoisted(() => LowerReturn(returned))); }
            else if (prong.ReturnsVoid) { stmts.Add(LowerReturnVoid()); }
            else if (prong.Jump is { } jump) { stmts.Add(LowerProngJump(jump)); }
        }
        finally
        {
            _symbols.ExitScope();
        }
        if (!EndsInJump(stmts)) { stmts.Add(new Break()); }   // no Zig fall-through
        return new SwitchSection(labels, new List<CStmt> { new Block(stmts) });
    }
    /// <summary>The sections of an <c>inline</c> prong of a runtime integer switch: the body instantiated once per case
    /// value (a range expands, up to 256 values), each with its <c>|x|</c> capture bound as a comptime constant of that
    /// value. <c>inline else</c> would need the subject type's whole value set, and is a loud cut.</summary>
    private List<SwitchSection> LowerInlineProngSections(Item innerProng, CExpr subject)
    {
        var prong = DecomposeProng(innerProng);
        if (prong.CaseVals.Content is Zig.CaseElse)
        {
            throw new IrUnsupportedException("zig `inline else =>` in a runtime switch is not supported yet (list the case values)");
        }
        long Value(Item item) => _ir.ConstEval(LowerExpr(item))
            ?? throw new IrUnsupportedException("zig `inline` prong: a case value must be comptime-known");
        var sections = new List<SwitchSection>();
        foreach (var (lo, hi) in WalkCaseValItems(prong.CaseVals))
        {
            var first = Value(lo);
            var last = hi is { } h ? Value(h) : first;
            if (last - first > 256) { throw new IrUnsupportedException("zig `inline` prong: a range of more than 256 values"); }
            for (var v = first; v <= last; v++)
            {
                _symbols.EnterScope();
                CStmt lowered;
                try
                {
                    if (prong.CaptureName is { } cap && cap != "_")
                    {
                        var capSym = _symbols.Declare(new Symbol { Name = cap, Kind = SymKind.Var, Type = subject.Type });
                        _comptimeVars[capSym] = (v, subject.Type);
                    }
                    lowered = LowerSelectedProng(prong);
                }
                finally
                {
                    _symbols.ExitScope();
                }
                var body = new List<CStmt> { lowered };
                if (!EndsInJump(body)) { body.Add(new Break()); }
                var label = new LitInt(v.ToString(CultureInfo.InvariantCulture), v) { Type = subject.Type };
                sections.Add(new SwitchSection(new List<SwitchLabel> { new SwitchLabel(label) }, body));
            }
        }
        return sections;
    }
    /// <summary>Lower a <c>switch</c> over a tagged union: switch on the <see cref="TagFieldName"/>
    /// discriminant, with <c>.variant</c> case labels resolving against the tag enum. A
    /// <c>|x|</c> payload capture binds <c>x</c> to the matched variant's payload field (by value),
    /// and a by-reference <c>|*x|</c> capture (Milestone M, part 4) binds <c>x</c> to a <c>*T</c>
    /// pointer INTO that payload field, so <c>x.* = …</c> writes through to the (mutable) union — at
    /// the top of that prong's block. The subject is hoisted to a temp first (unless it is already a
    /// simple variable) so each capture re-reads it without re-evaluating a side-effecting subject
    /// expression.</summary>
    private CStmt LowerUnionSwitch(CExpr subject, Item prongsItem, ZigUnionInfo info, Func<Item, CStmt>? fillValue = null)
    {
        // A VALUE switch (`const n: u8 = switch (spec) { .none => 1, .number => |v| v * 4 };`) passes
        // `fillValue`: each prong's value expression fills the result temp instead of being a statement.
        CStmt ProngValue(Item valueItem) => fillValue is { } fill ? fill(valueItem)
            : IsStatementIf(valueItem) ? LowerStatementIf(valueItem)   // `.lib => |n| if (n == w) return true,` (P3a)
            : new ExprStmt(LowerExpr(valueItem));
        var isPtr = subject.Type.Unqualified is CType.Pointer;
        var pre = new List<CStmt>();
        CExpr unionRef;
        if (subject is VarRef)
        {
            unionRef = subject;   // a bare variable — safe to re-reference per prong
        }
        else
        {
            var tmp = _symbols.Declare(new Symbol { Name = "__un", Kind = SymKind.Var, Type = subject.Type });
            pre.Add(new DeclStmt(new List<LocalDecl> { new(tmp, subject) }));
            unionRef = new VarRef(tmp) { Type = subject.Type, IsLValue = true };
        }
        var disc = new Member(unionRef, info.TagFieldName, isPtr) { Type = info.TagType, IsLValue = true };

        var sections = new List<SwitchSection>();
        foreach (var prongItem in Flatten(prongsItem))
        {
            // `inline .a, .b => |x| …` (std.json.static's `inline .number, .allocated_number, .string, .allocated_string =>
            // |slice| slice`, task #212): one prong per listed variant, so the capture takes each variant's own payload type
            // (a plain multi-variant capture requires them to share one). Without a capture it is the prong as written.
            if (prongItem.Content is Zig.InlineProng inlineProng)
            {
                var inlined = DecomposeProng(inlineProng.Arg1);
                if (inlined.CaseVals.Content is Zig.CaseElse)
                {
                    throw new IrUnsupportedException(
                        $"zig `inline else =>` in a switch over the tagged union '{info.Name}' is not supported yet (list the variants)");
                }
                if (inlined.CaptureName is null or "_")
                {
                    AddUnionSection(inlineProng.Arg1, null);
                    continue;
                }
                RejectUnionRange(inlined.CaseVals, info);
                foreach (var (variantItem, _) in WalkCaseValItems(inlined.CaseVals)) { AddUnionSection(inlineProng.Arg1, variantItem); }
                continue;
            }
            AddUnionSection(prongItem, null);
        }

        // One prong's section: its labels (or the single variant `onlyCase` an unrolled `inline` prong stands for), its
        // capture bound to that variant's payload, and its body.
        void AddUnionSection(Item prongItem, Item? onlyCase)
        {
            // A bare-expr prong (`=> expr`) in a union STATEMENT switch is an expression statement,
            // with no payload capture (capture needs a braced block); handle it up front.
            if (prongItem.Content is Zig.ProngExpr pe)
            {
                RejectUnionRange(pe.Arg0, info);
                var exprLabels = LowerCaseVals(pe.Arg0, info.TagType);
                var peBody = new List<CStmt> { ProngValue(pe.Arg2) };
                if (!EndsInJump(peBody)) { peBody.Add(new Break()); }
                sections.Add(new SwitchSection(exprLabels, peBody));
                return;
            }
            // A `return`-body prong (`.variant => return [e]`) without a capture — symmetric with the
            // non-union `LowerSwitch` (road-to-zig-std S9). `return` reuses the statement return-lowering
            // (error-union wrapping / errdefer). The capture-BODY prong forms (`|x| return e` / `|x| e`)
            // remain a follow-up — a capture there still hits the default loud cut below.
            if (prongItem.Content is Zig.ProngReturn or Zig.ProngReturnVoid)
            {
                Item rCase;
                List<CStmt> rBody;
                if (prongItem.Content is Zig.ProngReturn prr)
                {
                    rCase = prr.Arg0;
                    rBody = new List<CStmt> { Hoisted(() => LowerReturn(prr.Arg3)) };
                }
                else
                {
                    var prv = (Zig.ProngReturnVoid)prongItem.Content;
                    rCase = prv.Arg0;
                    rBody = new List<CStmt> { LowerReturnVoid() };
                }
                RejectUnionRange(rCase, info);
                var rLabels = LowerCaseVals(rCase, info.TagType);
                sections.Add(new SwitchSection(rLabels, rBody));   // `return` is a jump — no Break needed
                return;
            }
            // A prong body is a Block, or (road-to-zig-std S9) a bare expr / `return [e]`, each with an
            // optional `|x|` / `|*x|` payload capture. Decompose the shape once, then lower the body
            // INSIDE the capture scope so it sees the binding.
            Item caseVals; string? captureName; bool captureByRef;
            Item? blockBody = null, exprBody = null, returnBody = null, jumpBody = null;
            Zig.ProngAssign? assignBody = null;
            Zig.ProngCaptureAssign? captureAssignBody = null;
            var voidReturn = false;
            switch (prongItem.Content)
            {
                case Zig.Prong p:                     caseVals = p.Arg0; captureName = null;        captureByRef = false; blockBody  = p.Arg2; break;
                // `.off => total += 5` (task #109): the assignment prong the plain switch has (#64).
                case Zig.ProngAssign p:               caseVals = p.Arg0; captureName = null;        captureByRef = false; assignBody = p;      break;
                case Zig.ProngCaptureAssign p:        caseVals = p.Arg0; captureName = Tok(p.Arg3); captureByRef = false; captureAssignBody = p; break;
                case Zig.ProngCapture p:              caseVals = p.Arg0; captureName = Tok(p.Arg3); captureByRef = false; blockBody  = p.Arg5; break;
                case Zig.ProngCaptureRef p:           caseVals = p.Arg0; captureName = Tok(p.Arg4); captureByRef = true;  blockBody  = p.Arg6; break;
                case Zig.ProngCaptureExpr p:          caseVals = p.Arg0; captureName = Tok(p.Arg3); captureByRef = false; exprBody   = p.Arg5; break;
                case Zig.ProngCaptureReturn p:        caseVals = p.Arg0; captureName = Tok(p.Arg3); captureByRef = false; returnBody = p.Arg6; break;
                case Zig.ProngCaptureReturnVoid p:    caseVals = p.Arg0; captureName = Tok(p.Arg3); captureByRef = false; voidReturn = true;   break;
                case Zig.ProngCaptureRefExpr p:       caseVals = p.Arg0; captureName = Tok(p.Arg4); captureByRef = true;  exprBody   = p.Arg6; break;
                case Zig.ProngCaptureRefReturn p:     caseVals = p.Arg0; captureName = Tok(p.Arg4); captureByRef = true;  returnBody = p.Arg7; break;
                case Zig.ProngCaptureRefReturnVoid p: caseVals = p.Arg0; captureName = Tok(p.Arg4); captureByRef = true;  voidReturn = true;   break;
                case Zig.ProngJump p:                 caseVals = p.Arg0; captureName = null;        captureByRef = false; jumpBody   = p.Arg2; break;
                case Zig.ProngCaptureJump p:          caseVals = p.Arg0; captureName = Tok(p.Arg3); captureByRef = false; jumpBody   = p.Arg5; break;
                default: throw new IrUnsupportedException("zig switch prong: " + (prongItem.Content?.GetType().Name ?? "null"));
            }
            if (onlyCase is null) { RejectUnionRange(caseVals, info); }
            // `.variant` → EnumConstRef(U_Tag.variant); an unrolled `inline` prong has the one variant it stands for.
            var labels = onlyCase is { } one
                ? new List<SwitchLabel> { new SwitchLabel(CaseLabelValue(one, info.TagType)) }
                : LowerCaseVals(caseVals, info.TagType);

            // Lower the body statements; a `return` reuses the statement return-lowering (error-union
            // wrapping / errdefer). For a block the statements are flattened (so a leading capture decl
            // sits in the same scope); the other forms are a single statement.
            List<CStmt> LowerProngBody() =>
                blockBody is not null    ? new List<CStmt>(LowerBlock(blockBody).Stmts)
                : exprBody is not null   ? new List<CStmt> { ProngValue(exprBody) }
                : returnBody is not null ? new List<CStmt> { Hoisted(() => LowerReturn(returnBody)) }
                : voidReturn             ? new List<CStmt> { LowerReturnVoid() }
                : jumpBody is not null   ? new List<CStmt> { LowerProngJump(jumpBody) }
                : assignBody is not null ? new List<CStmt> { LowerProngAssign(assignBody) }
                : captureAssignBody is not null
                    ? new List<CStmt> { LowerAssignProngBody(captureAssignBody.Arg5, captureAssignBody.Arg6, captureAssignBody.Arg7) }
                : throw new IrUnsupportedException("zig switch capture prong has no body");

            List<CStmt> body;
            if (captureName is not null && captureName != "_")
            {
                var variant = CaptureVariantName(onlyCase ?? caseVals, info, captureName);
                var payloadType = info.Variants[variant]
                    ?? throw new IrUnsupportedException(
                        $"union '{info.Name}' variant '{variant}' is a void variant — it has no payload to capture with `|{captureName}|`");
                using var symbolScope = EnterSymbolScope();
                // By-value (`|x|`): `var x = __un.__payload.variant;` (a copy). By-reference (`|*x|`):
                // `T* x = &(__un.__payload.variant);` — a pointer into the union's payload field, so
                // `x.* = …` writes through to the (mutable) union value.
                var bindType = captureByRef ? new CType.Pointer(payloadType) : payloadType;
                var capSym = _symbols.Declare(new Symbol { Name = captureName, Kind = SymKind.Var, Type = bindType });
                var payloadBase = new Member(unionRef, info.PayloadFieldName, isPtr) { Type = new CType.Named(info.PayloadTypeName!), IsLValue = true };
                var payloadField = new Member(payloadBase, variant, false) { Type = payloadType, IsLValue = true };
                CExpr capInit = captureByRef ? new Unary(UnOp.AddrOf, payloadField) { Type = bindType } : payloadField;
                if (captureByRef && unionRef is VarRef { Sym: { } uvar }) { uvar.AddressTaken = true; }
                var inner = LowerProngBody();
                symbolScope.Dispose();
                var combined = new List<CStmt> { new DeclStmt(new List<LocalDecl> { new(capSym, capInit) }) };
                combined.AddRange(inner);
                body = new List<CStmt> { new Block(combined) };
            }
            else
            {
                body = blockBody is not null ? new List<CStmt> { LowerBlock(blockBody) } : LowerProngBody();
            }
            if (!EndsInJump(body)) { body.Add(new Break()); }   // no Zig fall-through
            sections.Add(new SwitchSection(labels, body));
        }

        // A Zig union switch is exhaustive; C# can't prove a tag switch covers every case, so
        // without an `else` it would reject the enclosing function ("not all code paths return",
        // CS0161). Make the LAST prong the `default` — for an exhaustive switch (which valid Zig
        // requires) the last variant's tag is the only value that reaches it, so this is
        // semantics-preserving and needs no synthetic statement.
        if (sections.Count > 0 && !sections.Any(s => s.Labels.Any(l => l.CaseExpr is null)))
        {
            sections[^1] = sections[^1] with { Labels = new List<SwitchLabel> { new SwitchLabel(null) } };
        }

        var sw = new Switch(disc, sections);
        if (pre.Count == 0) { return sw; }
        pre.Add(sw);
        return new Block(pre);   // { var __un = subject; switch (__un.__tag) { … } }
    }
    /// <summary>The payload <c>.variant</c> a tagged-union capture prong binds. A single-variant prong
    /// (<c>.a =&gt; |x|</c>) returns that variant. A MULTI-variant capture prong (<c>.a, .b =&gt; |x|</c>,
    /// Milestone Z) is allowed only when every listed variant shares the SAME payload type — then the
    /// FIRST variant's payload field is bound: in the explicit-layout payload union every variant
    /// overlaps at offset 0, so reading one (the field of the same type) aliases whichever variant
    /// actually matched. An <c>else</c>, an unknown variant, or variants with differing payload types is
    /// rejected (a capture binds to one <c>|x|</c>, so one payload type).</summary>
    private string CaptureVariantName(Item caseVals, ZigUnionInfo info, string captureName)
    {
        if (caseVals.Content is Zig.CaseElse)
        {
            throw new IrUnsupportedException(
                $"a tagged-union capture prong (`|{captureName}|`) cannot capture on `else` — it has no single payload type");
        }
        var vals = Flatten(caseVals);
        var variants = new List<string>(vals.Count);
        foreach (var v in vals)
        {
            if (v.Content is not Zig.EnumLit el)
            {
                throw new IrUnsupportedException(
                    "a tagged-union capture prong must list `.variant` values");
            }
            var name = Tok(el.Arg1);
            if (!info.Variants.ContainsKey(name))
            {
                throw new IrUnsupportedException($"union '{info.Name}' has no variant '{name}'");
            }
            variants.Add(name);
        }
        // A multi-variant capture binds to a single `|x|`, so every listed variant must carry the
        // same payload type. The first variant's payload field aliases the rest (all at offset 0).
        var first = variants[0];
        var firstType = info.Variants[first];
        for (var i = 1; i < variants.Count; i++)
        {
            var ti = info.Variants[variants[i]];
            if (firstType is null || ti is null || !firstType.Unqualified.Equals(ti.Unqualified))
            {
                throw new IrUnsupportedException(
                    $"a multi-variant capture prong `.{first}, .{variants[i]} => |{captureName}|` requires every " +
                    "listed variant to share the same payload type");
            }
        }
        return first;
    }
    /// <summary>Lower a prong's case values to switch labels: <c>else</c> → the single null
    /// (default) label; otherwise each comma-separated element → one label, lowered against the
    /// subject type as its sink (so a <c>.member</c> case resolves when switching on an enum). An
    /// inclusive range element <c>lo...hi</c> (Milestone L, part 4) becomes a range label
    /// (<see cref="SwitchLabel.HiExpr"/> set) → a relational pattern in the backend.</summary>
    private List<SwitchLabel> LowerCaseVals(Item caseVals, CType? sink)
    {
        if (caseVals.Content is Zig.CaseElse)
        {
            return new List<SwitchLabel> { new SwitchLabel(null) };
        }
        var labels = new List<SwitchLabel>();
        foreach (var (lo, hi) in WalkCaseValItems(caseVals))
        {
            labels.Add(hi is null
                ? new SwitchLabel(CaseLabelValue(lo, sink))
                : new SwitchLabel(CaseLabelValue(lo, sink), CaseLabelValue(hi, sink)));
        }
        return labels;
    }
    /// <summary>One case value: lowered at the subject type, and an integer one that names a comptime const
    /// (std.sort.pdq's `max_swaps => .decreasing`, `const max_swaps = 4 * 3;`) folded to a literal, since a C# case
    /// label must be a constant of the subject's type.</summary>
    private CExpr CaseLabelValue(Item item, CType? sink)
    {
        var lowered = LowerExprSink(item, sink);
        if (lowered is not LitInt && sink?.Unqualified is CType.Prim { Integer: true } && _ir.ConstEval(lowered) is { } v)
        {
            return new LitInt(v.ToString(System.Globalization.CultureInfo.InvariantCulture), v) { Type = sink };
        }
        return lowered;
    }
    /// <summary>Walk a (non-<c>else</c>) <c>CaseVals</c> comma-list into its elements, each a single
    /// value (<c>Hi</c> null) or an inclusive range <c>lo...hi</c> (<c>Hi</c> set). Mirrors the
    /// grammar's right-recursive list shape over the plain (<c>CaseVals…</c>) and range
    /// (<c>CaseRange…</c>) productions.</summary>
    private static List<(Item Lo, Item? Hi)> WalkCaseValItems(Item caseVals)
    {
        var items = new List<(Item, Item?)>();
        var it = caseVals;
        while (true)
        {
            switch (it.Content)
            {
                case Zig.CaseValsCons c:  items.Add((c.Arg0, null));   it = c.Arg2; continue;  // [Expr ',' CaseVals]
                case Zig.CaseValsOne o:   items.Add((o.Arg0, null));   return items;           // [Expr]
                case Zig.CaseValsTrail t: items.Add((t.Arg0, null));   return items;           // [Expr ','] trailing comma
                case Zig.CaseRangeTrail r: items.Add((r.Arg0, r.Arg2)); return items;          // [Expr '...' Expr ',']
                case Zig.CaseRangeCons r: items.Add((r.Arg0, r.Arg2)); it = r.Arg4; continue;  // [Expr '...' Expr ',' CaseVals]
                case Zig.CaseRangeOne r:  items.Add((r.Arg0, r.Arg2)); return items;           // [Expr '...' Expr]
                default:
                    throw new IrUnsupportedException(
                        "zig switch case values: " + (it.Content?.GetType().Name ?? "null"));
            }
        }
    }
    /// <summary>True when a <c>CaseVals</c> list contains an inclusive range element
    /// (<c>lo...hi</c>) — used to reject ranges where they aren't supported yet (a switch
    /// EXPRESSION arm, a tagged-union switch).</summary>
    private static bool CaseValsContainsRange(Item caseVals) => caseVals.Content switch
    {
        Zig.CaseRangeOne or Zig.CaseRangeCons or Zig.CaseRangeTrail => true,
        Zig.CaseValsCons c => CaseValsContainsRange(c.Arg2),
        _ => false,
    };
    /// <summary>Reject an inclusive range in a tagged-union switch prong — a union's variants are
    /// not ordered, so <c>.a...c</c> is meaningless (and not valid Zig).</summary>
    private static void RejectUnionRange(Item caseVals, ZigUnionInfo info)
    {
        if (CaseValsContainsRange(caseVals))
        {
            throw new IrUnsupportedException(
                $"an inclusive range (`lo...hi`) isn't valid in a switch on the tagged union '{info.Name}' (its variants aren't ordered)");
        }
    }
    /// <summary>Lower a switch EXPRESSION `switch (subj) { v => e, …, else => e }` (Milestone L) to
    /// the C# switch-expression IR (<see cref="SwitchExpr"/>). Each prong must YIELD a value — a
    /// bare-expr body `v => e` lowered at the result <paramref name="sink"/> (so a nested `.member`
    /// / `.{…}` / cast resolves). `else` → the `_` default arm; a multi-value prong `a, b => e`
    /// becomes one arm with both labels (rendered `a or b`). The subject is lowered once. Deferred
    /// (clear error): a block-bodied prong (`=> { … break :blk v; }`, needs the labeled-block
    /// increment) and a tagged-union payload capture `|x|` in expression position.</summary>
    private CExpr LowerSwitchExpr(Item subjectItem, Item prongsItem, CType? sink)
    {
        // A COMPTIME subject folds to the selected prong's value (road-to-zig-std S5) — which is also
        // what makes a `|i|` capture work in expression position, where a RUNTIME switch expression
        // still can't bind one (there is nothing to bind at comptime: the payload is the fold).
        if (SelectComptimeProng(subjectItem, prongsItem, out var ctPayload) is { } ctProng)
        {
            if (ctProng.Expr is not { } ctValue)
            {
                throw new IrUnsupportedException(
                    "zig `switch (@typeInfo(T))` in value position: the selected prong must yield a value "
                    + "(`.int => expr`); a block-bodied prong needs a `const`/`return` statement context");
            }
            EnterComptimeProng(ctProng, ctPayload);
            try { return LowerExprSink(ctValue, sink); }
            finally { ExitComptimeProng(); }
        }
        if (TryFoldComptimeIntSwitch(subjectItem, prongsItem, sink) is { } folded) { return folded; }
        var subjectImpure = _hoistImpureSeen;
        var subject = LowerExpr(subjectItem);
        var arms = new List<SwitchExprArm>();
        // What each arm hoisted: an arm runs only when selected, so its statements stay with it (task #203).
        var armHoists = new List<List<CStmt>>();
        foreach (var prongItem in Flatten(prongsItem))
        {
            if (prongItem.Content is not Zig.ProngExpr pe)
            {
                throw new IrUnsupportedException(
                    "zig switch-expression prong must yield a value (`v => expr`); a block-bodied prong " +
                    "(a labeled `break :blk v`) is supported only as a full `const`/`var`/`return`/assignment RHS " +
                    "(Milestone Y, part 1), not in a sub-expression; a `|x|` capture in a switch expression is not supported yet");
            }
            var (value, hoisted) = LowerArmIsolated(() => LowerExprSink(pe.Arg2, sink));
            armHoists.Add(hoisted);
            // `else` → the `_` default arm; otherwise the prong's case values become the arm's
            // labels (a multi-value prong → several, rendered `a or b`), reusing LowerCaseVals so an
            // inclusive range `lo...hi` lowers to a relational-pattern label exactly as in a
            // statement switch.
            arms.Add(pe.Arg0.Content is Zig.CaseElse
                ? new SwitchExprArm(null, value)
                : new SwitchExprArm(LowerCaseVals(pe.Arg0, subject.Type), value));
        }
        // A Zig switch over an error set or enum is exhaustive — real zig proves the prongs cover
        // every member, so no `else` is required. dotcc erases an error set to a flat `ushort` and
        // an enum value can hold any backing int, so C# can't prove coverage and rejects the switch
        // EXPRESSION (CS8509 "not all values covered"). Mirror the union-switch fix: with no `else`,
        // collapse the LAST arm to the `_` default — for an exhaustive switch (which valid Zig
        // requires) only that arm's values reach it, so it is semantics-preserving. (Milestone X,
        // part 3b.) A plain integer subject is left alone: there an `else` IS required, so a missing
        // default reflects a genuinely non-exhaustive switch.
        if (subject.Type.Unqualified is CType.ErrorSetType or CType.Enum
            && arms.Count > 0
            && !arms.Any(a => a.Labels is null))
        {
            arms[^1] = arms[^1] with { Labels = null };
        }
        // The result type is the sink, else inferred from the first value-yielding arm.
        var resultType = sink
            ?? arms.Select(a => a.Value.Type).FirstOrDefault(t => t is not null)
            ?? CType.Int;
        if (armHoists.Any(h => h.Count > 0))
        {
            return HoistedValueSwitch(subject, subjectImpure, arms, armHoists,
                HoistedResultType(sink, resultType, arms.Select(a => a.Value)));
        }
        return new SwitchExpr(subject, arms) { Type = resultType };
    }
    /// <summary>A switch EXPRESSION over a compile-time-known integer whose prongs a runtime C# switch
    /// expression cannot carry (a <c>|x|</c> capture): std.math.IntFittingRange's
    /// <c>switch (to) { 0 =&gt; 0, else =&gt; |pos_max| 1 + log2(pos_max) }</c>. The subject and every case value
    /// evaluate at compile time, the matching prong's value is lowered with its capture bound to the subject,
    /// and nothing else is. Null when every prong is a plain <c>v =&gt; e</c> (the runtime lowering handles it,
    /// unchanged) or the subject is not comptime-known.</summary>
    private CExpr? TryFoldComptimeIntSwitch(Item subjectItem, Item prongsItem, CType? sink)
    {
        var prongs = Flatten(prongsItem);
        // An all-value switch stays a runtime C# switch, unless a prong is a `@compileError` (std.math.floatMantissaBits'
        // `else => @compileError("unknown floating point type …")`): zig never analyses an unselected prong, so a
        // comptime-known subject must select before any other prong lowers.
        if (prongs.All(p => p.Content is Zig.ProngExpr) && !prongs.Any(p => p.Content is Zig.ProngExpr { Arg2.Content: Zig.BuiltinCall { Arg2: not null } cb }
                                                                            && Tok(cb.Arg0) == "@compileError"))
        {
            return null;
        }
        IrModule.CtInt? Eval(Item item)
        {
            using (EnterThrowawayHoist())
            {
                try { return _ir.EvalComptimeValue(LowerExpr(item)) as IrModule.CtInt; }
                catch (IrUnsupportedException) { return null; }
            }
        }
        if (Eval(subjectItem) is not { } subject) { return null; }
        ZigProng? chosen = null;
        foreach (var prongItem in prongs)
        {
            var prong = DecomposeProng(prongItem);
            if (prong.CaseVals.Content is Zig.CaseElse) { chosen ??= prong; continue; }
            foreach (var (lo, hi) in WalkCaseValItems(prong.CaseVals))
            {
                if (Eval(lo) is not { } low || (hi is { } h ? Eval(h) : low) is not { } high) { return null; }
                if (subject.Value >= low.Value && subject.Value <= high.Value) { chosen = prong; goto Selected; }
            }
        }
        Selected:
        if (chosen is null)
        {
            throw new IrUnsupportedException(
                $"zig `switch` over the comptime value {subject.Value}: no prong matches it and there is no `else`");
        }
        if (chosen.Expr is not { } value)
        {
            throw new IrUnsupportedException(
                "zig `switch` over a comptime integer in value position: the selected prong must yield a value (`v => expr`)");
        }
        if (chosen.CaptureName is not { } capture || capture == "_") { return LowerExprSink(value, sink); }
        var prev = _comptimeValues.GetValueOrDefault(capture);
        _comptimeValues[capture] = _ir.SpliceComptimeValue(subject)
            ?? throw new IrUnsupportedException($"zig `switch` capture `|{capture}|`: the comptime subject has no literal form");
        try { return LowerExprSink(value, sink); }
        finally
        {
            if (prev is { } p) { _comptimeValues[capture] = p; } else { _comptimeValues.Remove(capture); }
        }
    }
}
