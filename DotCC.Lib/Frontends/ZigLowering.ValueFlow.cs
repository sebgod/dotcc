#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Control flow as a VALUE: an <c>if</c>, <c>switch</c> or loop whose arms are statements or jumps,
/// lowered to statements that fill a result temp, and the no-return and error-union rules its arms follow. One
/// concern of the <see cref="ZigLowering"/> binder.</summary>
internal sealed partial class ZigLowering
{
    /// <summary>A result temp shared while a value-position <c>if</c>/<c>switch</c> is lowered as a
    /// statement (Milestone Y, part 1). Every branch fills <see cref="Temp"/>; <see cref="ResultType"/>
    /// is the sink when known, else fixed by the first branch's value type (so a sink-less
    /// <c>const x = switch …</c> still types the temp).</summary>
    private sealed class ValueTempTarget
    {
        public required Symbol Temp;
        public CType? ResultType;
    }
    /// <summary>True when <paramref name="rhs"/> is a value-position <c>if</c>/<c>switch</c> EXPRESSION
    /// with a branch that needs STATEMENTS to produce its value — a labeled value-block branch
    /// (<c>blk: {…; break :blk v;}</c>) or a block-bodied / capturing switch prong. Such a form can't
    /// be a C# expression (a ternary / switch-expression), so a statement context (a <c>const</c> /
    /// <c>var</c> / <c>return</c> / assignment RHS) lowers it via <see cref="LowerValueControlFlowStmt"/>
    /// into a result temp. An all-simple-value <c>if</c>/<c>switch</c> returns false and keeps the clean
    /// expression lowering (the C# ternary / switch-expression).</summary>
    private static bool IsValueControlFlowStmt(Item rhs) => rhs.Content switch
    {
        Zig.IfElseExpr { Arg4: null, Arg7: null } e             => IsLabeledValue(e.Arg5) || IsLabeledValue(e.Arg8),
        Zig.SwitchExpr s         => SwitchExprNeedsStmt(s.Arg5, s.Arg2),
        // `comptime switch` / `comptime if`: the inner form decides. The `comptime` asks zig to evaluate
        // it at compile time; dotcc's lowering already folds the arm whenever the subject is
        // comptime-known, and a runtime subject (which zig rejects here) keeps its runtime lowering.
        Zig.PreComptime { Arg1.Content: Zig.SwitchExpr } c => IsValueControlFlowStmt(c.Arg1),
        Zig.PreComptime { Arg1.Content: Zig.IfElseExpr } c => IsValueControlFlowStmt(c.Arg1),
        // A value-position loop (`while/for … else`, Milestone Y part 2) ALWAYS needs the statement
        // lowering — a loop that yields via `break v` / an `else` value can't be a C# expression.
        Zig.WhileValue or Zig.ForValue or Zig.InlineForValue or Zig.LabeledLoopExpr => true,
        _ => false,
    };
    /// <summary>True when any prong of a switch EXPRESSION needs statements to yield its value — a
    /// block-bodied (<c>=&gt; { … }</c>) or capturing (<c>=&gt; |x| { … }</c>) prong, or a bare-expr
    /// prong whose value is itself a labeled value-block (<c>=&gt; blk: { … break :blk v; }</c>), or a <c>|v| expr</c>
    /// capture prong over a runtime <paramref name="subjectItem"/> (a union's payload).</summary>
    private static bool SwitchExprNeedsStmt(Item prongsItem, Item subjectItem) =>
        Flatten(prongsItem).Any(p =>
        {
            var view = DecomposeProng(p);
            // `inline .a, .b => |x| …` (task #212) unrolls into one prong per listed value, and `.number => |v| v * 4` over a
            // union binds a payload: both need statements. Over a comptime `@typeInfo(T)` the selected prong folds where
            // the expression lowers, so it stays one.
            if (p.Content is Zig.InlineProng || view.CaptureName is not null && view.Expr is not null)
            {
                return subjectItem.Content is not Zig.BuiltinCall { Arg2: not null, Arg0: var sb } || Tok(sb) != "@typeInfo";
            }
            // A value prong needs statements only for a labeled value block; every other body (a block, a `comptime { … }`
            // block that returns, std.math.log2; a `return`, std.fmt.charToDigit; a jump; an assignment; a loop) is one.
            return view.Expr is { } value ? IsLabeledValue(value) || IsValueControlFlowStmt(value) : true;
        });
    /// <summary>Lower a value-position control-flow form that needs statements to produce its value
    /// (see <see cref="IsValueControlFlowStmt"/>) as a C# STATEMENT, then hand the result temp to
    /// <paramref name="consume"/> (the decl / return / assignment that reads it). Dispatches an
    /// <c>if</c>/<c>switch</c> branch-temp-fill (Milestone Y, part 1) and a <c>while/for … else</c>
    /// value loop (part 2) to their builders.</summary>
    private CStmt LowerValueControlFlowStmt(Item rhs, CType? sink, Func<Symbol, CStmt> consume) => rhs.Content switch
    {
        Zig.IfElseExpr { Arg4: null, Arg7: null } or Zig.SwitchExpr => LowerValueIfSwitch(rhs, sink, consume),
        Zig.PreComptime { Arg1.Content: Zig.SwitchExpr } c => LowerValueControlFlowStmt(c.Arg1, sink, consume),
        Zig.PreComptime { Arg1.Content: Zig.IfElseExpr } c => LowerValueControlFlowStmt(c.Arg1, sink, consume),
        Zig.WhileValue or Zig.ForValue or Zig.InlineForValue or Zig.LabeledLoopExpr
            => LowerLoopValue(rhs, sink, consume),
        _ => throw new IrUnsupportedException(
            "internal: value control-flow statement on " + (rhs.Content?.GetType().Name ?? "null")),
    };
    /// <summary>Lower a value-position <c>if</c>/<c>switch</c> whose branch(es) need statements as a C#
    /// STATEMENT that fills a result temp. Mirrors the labeled-value-block temp-fill
    /// (<see cref="LowerLabeledValueBlock"/>): a default-initialized temp, each branch assigning it,
    /// then the consumer. The temp's type is the <paramref name="sink"/> when known, else the first
    /// branch's value type. (Milestone Y, part 1.) A union-subject value-switch and a <c>|x|</c>
    /// capture in expression position stay clear deferred errors.</summary>
    private CStmt LowerValueIfSwitch(Item rhs, CType? sink, Func<Symbol, CStmt> consume)
    {
        var n = _blockLabelCounter++;
        var temp = _symbols.Declare(new Symbol { Name = "__vcf" + n, Kind = SymKind.Var, Type = sink ?? CType.Int });
        var rt = new ValueTempTarget { Temp = temp, ResultType = sink };
        // The cond/subject is lowered before the branches (left-to-right C# argument evaluation), and
        // the first branch's FillValueTemp fixes rt.ResultType so a sink-less switch/if still types.
        CStmt filler = rhs.Content switch
        {
            Zig.IfElseExpr { Arg4: null, Arg7: null } e when TryFoldComptimeCondition(e.Arg2) is { } taken => FillValueTemp(taken ? e.Arg5 : e.Arg8, rt),
            Zig.IfElseExpr { Arg4: null, Arg7: null } e => new If(LowerExpr(e.Arg2),
                                   new Block(new List<CStmt> { FillValueTemp(e.Arg5, rt) }),
                                   new Block(new List<CStmt> { FillValueTemp(e.Arg8, rt) })),
            Zig.SwitchExpr s         => BuildValueSwitch(s.Arg2, s.Arg5, rt),
            _ => throw new IrUnsupportedException(
                "internal: value if/switch on " + (rhs.Content?.GetType().Name ?? "null")),
        };
        var resultType = rt.ResultType
            ?? throw new IrUnsupportedException("a value-position `if`/`switch` must yield a value in every branch");
        temp.Type = resultType;
        return new Seq(new List<CStmt>
        {
            // Default-initialized so C# definite-assignment is satisfied even though every real path
            // assigns the temp (a switch with no matching case is impossible in valid, exhaustive Zig).
            new DeclStmt(new List<LocalDecl> { new(temp, new DefaultLit { Type = resultType }) }),
            filler,
            consume(temp),
        });
    }
    /// <summary>Lower a value-position loop <c>while/for (…) { … } else v</c> (Milestone Y, part 2) as
    /// a C# STATEMENT filling a result temp. The loop runs normally; a <c>break v</c> (unlabeled, the
    /// innermost value loop) or <c>break :lbl v</c> (the matching labeled one) inside assigns the temp
    /// and jumps to the end label, SKIPPING the <c>else</c> value — which is assigned only on natural
    /// completion (no break). The end label is emitted only if a <c>break</c> targeted it (an else-only
    /// loop never jumps there). The temp's type is the sink when known, else the first <c>break</c> /
    /// the <c>else</c> value type. An <c>else</c> that never completes (a jump, a <c>return</c>, a <c>{ … }</c> block)
    /// runs as a statement, so the loop's value is its <c>break</c>s' alone.</summary>
    private CStmt LowerLoopValue(Item rhs, CType? sink, Func<Symbol, CStmt> consume)
    {
        string? label = null;
        Item condOrIter, blockItem;
        Item? elseItem;
        string? elemName = null;
        var byRef = false;
        // `inline for` (task #131): the comptime lists and captures to unroll over, instead of a runtime loop.
        (Item Objs, Item Caps, bool Multi, Item? SingleObj, string? SingleCap)? inlineFor = null;
        // A range / multi-object `for` or a capture or continue-clause `while`: the statement loop's own lowering, built while
        // the value target is active.
        Func<CStmt>? statementLoop = null;
        // `lbl: for … else …` / `lbl: while …`: the label names the loop's value target (`break :lbl v`).
        var loopItem = rhs;
        if (rhs.Content is Zig.LabeledLoopExpr labeled)
        {
            label = Tok(labeled.Arg0);
            loopItem = labeled.Arg2;
        }
        switch (loopItem.Content)
        {
            case Zig.WhileValue w:
            {
                var (capture, captureByRef, _) = PayloadParts(w.Arg4);
                if (captureByRef) { throw new IrUnsupportedException("zig value `while (opt) |*x|`: a by-reference while capture is not supported yet"); }
                if (w.Arg7?.Content is Zig.LoopElse { Arg1: not null })
                {
                    throw new IrUnsupportedException("zig value `while (eu) |v| … else |e| …`: an error capture on a value loop's `else` is not supported yet");
                }
                condOrIter = w.Arg2; blockItem = w.Arg6; elseItem = LoopElseArm(w.Arg7);
                var contPost = ContPostOf(w.Arg5);
                // `while (it) |node| { … } else …` (heap/ArenaAllocator.zig); `while (i < n) : (i += 1) { … } else true`
                // (std.bit_set.ArrayBitSet.eql): the C `For` with that post, so a `continue` runs it (as the statement form).
                if (capture is { } cap) { statementLoop = () => LowerWhileCapture(w.Arg2, cap, w.Arg6, null, contPost); }
                else if (contPost is { } post) { statementLoop = () => new For(null, LowerExpr(w.Arg2), post(), LowerStmt(w.Arg6)); }
                break;
            }
            case Zig.ForValue f:
            {
                var (objects, captures) = DecomposeForMulti(f.Arg2, f.Arg6);
                condOrIter = f.Arg2; blockItem = f.Arg9; elseItem = LoopElseArm(f.Arg10, forLoop: true);
                if (objects is [{ IsRange: false } one])
                {
                    condOrIter = one.Expr;
                    (elemName, byRef) = captures[0];
                }
                else
                {
                    // A range (`for (0..amt) |i| { … } else 0`, crypto/bcrypt.zig) or several objects (`for (data, 0..)
                    // |buffer, i| { … } else …`, Io/Dispatch.zig): the statement loop under the value target.
                    statementLoop = () => LowerForMulti(f.Arg2, f.Arg6, f.Arg9);
                }
                break;
            }
            case Zig.InlineForValue f:
            {
                var (objects, captures) = DecomposeForMulti(f.Arg3, f.Arg7);
                condOrIter = f.Arg3; blockItem = f.Arg10; elseItem = LoopElseArm(f.Arg11, forLoop: true);
                inlineFor = (f.Arg3, f.Arg7, objects.Count > 1, objects[0].Expr, captures[0].Name);
                break;
            }
            default: throw new IrUnsupportedException("internal: loop-value on " + (rhs.Content?.GetType().Name ?? "null"));
        }

        var n = _loopValueCounter++;
        var endLabel = "__lv" + n + "_end";
        var temp = _symbols.Declare(new Symbol { Name = "__lv" + n, Kind = SymKind.Var, Type = sink ?? CType.Int });
        var target = new LoopValueTarget { Temp = temp, EndLabel = endLabel, Label = label, Sink = sink, ResultType = sink };

        // Lower the loop with the value target active so a `break v` inside resolves to it. The cond /
        // iterable is lowered before the body (it can't `break`), so it never references the temp.
        _loopValues.Push(target);
        // An `inline for` unrolls over its comptime lists (a `break v` in any copy fills the temp and jumps to the end); a
        // `for` names its element capture; a `while` has none.
        CStmt loop = statementLoop is { } buildLoop
            ? buildLoop()
            : inlineFor is { } unrolled
            ? unrolled.Multi
                ? UnrollComptimeMultiFor(unrolled.Objs, unrolled.Caps, blockItem)
                : unrolled.SingleObj is { } singleObj && TryComptimeIterable(singleObj, out var inlineList)
                    ? UnrollComptimeFor(new[] { (inlineList, unrolled.SingleCap ?? "_") }, blockItem)
                    : throw new IrUnsupportedException(
                        "a value-position `inline for` must walk a comptime list (a `@typeInfo` member list or a `[_]type{…}`)")
            : elemName is { } elem
            ? LowerForSlice(LowerExpr(condOrIter), elem, null, blockItem, byRef)
            : new While(LowerExpr(condOrIter), LowerStmt(blockItem));
        _loopValues.Pop();

        // `… else return v` / `… else break :outer …` / `… else { continue; }`: normal completion leaves by a jump, so the
        // loop's value is its `break`s' alone, and the code after the loop is reached only through the end label.
        if (elseItem is null || elseItem.Content is Zig.Block or Zig.VoidValue || IsNoreturnArm(elseItem))
        {
            var breakType = target.ResultType
                ?? throw new IrUnsupportedException("a value-position loop whose `else` never completes must yield its value with `break v`");
            temp.Type = breakType;
            return new Seq(new List<CStmt>
            {
                new DeclStmt(new List<LocalDecl> { new(temp, new DefaultLit { Type = breakType }) }),
                loop,
                elseItem is null ? new Seq(new List<CStmt>()) : LowerArmStmt(elseItem),
                new Labeled(endLabel, new Block(new List<CStmt>())),
                consume(temp),
            });
        }

        // The `else` value supplies the result on NORMAL completion. A `break v` jumped to `endLabel`,
        // skipping this. Sink it at the now-known result type (a `break` may have fixed it).
        var elseSink = target.ResultType ?? target.Sink;
        var elseVal = elseSink is { } sk ? LowerExprSink(elseItem, sk) : LowerExpr(elseItem);
        target.ResultType ??= elseVal.Type;
        var resultType = target.ResultType;
        temp.Type = resultType;

        var stmts = new List<CStmt>
        {
            new DeclStmt(new List<LocalDecl> { new(temp, new DefaultLit { Type = resultType }) }),
            loop,
            new ExprStmt(new Assign(null, LvRef(target), elseVal) { Type = resultType }),
        };
        if (target.BreakUsed) { stmts.Add(new Labeled(endLabel, new Block(new List<CStmt>()))); }
        stmts.Add(consume(temp));
        return new Seq(stmts);
    }
    /// <summary>The arm of a value loop's optional <c>else</c> (<c>LoopElse</c>), or null: with no <c>else</c> the loop's value is
    /// its <c>break</c>s' alone (<c>while (true) { … break v; … }</c>; zig rejects such a loop when it can end). A <c>for</c>'s
    /// <c>else</c> takes no capture.</summary>
    private static Item? LoopElseArm(Item? loopElse, bool forLoop = false)
    {
        if (loopElse?.Content is not Zig.LoopElse e) { return null; }
        if (forLoop && e.Arg1 is not null) { throw new CompileException("zig: a `for` loop's `else` takes no capture"); }
        return e.Arg2;
    }
    /// <summary>An lvalue reference to a value-loop result temp at its (now-resolved) type.</summary>
    private static VarRef LvRef(LoopValueTarget t) => new VarRef(t.Temp) { Type = t.ResultType!, IsLValue = true };
    /// <summary>Lower a <c>break v</c> targeting <paramref name="target"/>: assign the value to the
    /// loop's result temp, then <c>goto</c> its end label (skipping the loop's <c>else</c>). A braced
    /// <see cref="Block"/> (not a brace-less <see cref="Seq"/>) so a conditional break (`if (c) break v;`)
    /// keeps both the assign and the goto guarded. (Milestone Y, part 2.)</summary>
    private CStmt BuildLoopBreakValue(LoopValueTarget target, Item valueItem)
    {
        var value = (target.ResultType ?? target.Sink) is { } sk ? LowerExprSink(valueItem, sk) : LowerExpr(valueItem);
        target.ResultType ??= value.Type;
        target.BreakUsed = true;
        return new Block(new List<CStmt>
        {
            new ExprStmt(new Assign(null, LvRef(target), value) { Type = target.ResultType }),
            new Goto(target.EndLabel),
        });
    }
    /// <summary>Build the statement that fills a value-control-flow result temp from one branch's value
    /// (Milestone Y, part 1). A labeled value-block branch (<c>blk: { … break :blk v; }</c>) is
    /// temp-filled — its <c>break :blk v</c> assigns its own temp — and that temp copied into
    /// <paramref name="rt"/>'s; any other expression is lowered at the running result type and assigned.
    /// The first branch lowered fixes <see cref="ValueTempTarget.ResultType"/>.</summary>
    private CStmt FillValueTemp(Item valueItem, ValueTempTarget rt)
    {
        // An `unreachable` prong (`error.CodepointTooLarge => unreachable` in std.unicode's utf16LeToUtf8Impl, task #188) has
        // no value to store: it is the trap, as a statement. (Assigned, its `void` call was CS0029.)
        if (IsUnreachableItem(valueItem)) { return new ExprStmt(LowerExpr(valueItem)); }
        // A value loop (`=> for (…) |m| { … break .other; } else .tuple,`, std's zig/Ast/Render.zig) fills its own temp.
        if (valueItem.Content is Zig.WhileValue or Zig.ForValue or Zig.InlineForValue or Zig.LabeledLoopExpr)
        {
            return LowerLoopValue(valueItem, rt.ResultType, loopTemp =>
            {
                rt.ResultType ??= loopTemp.Type;
                return new ExprStmt(new Assign(null, RtRef(rt), new VarRef(loopTemp) { Type = loopTemp.Type }) { Type = rt.ResultType });
            });
        }
        if (IsLabeledValue(valueItem))
        {
            return LowerLabeledValue(valueItem, rt.ResultType, blkTemp =>
            {
                rt.ResultType ??= blkTemp.Type;
                return new ExprStmt(new Assign(null, RtRef(rt), new VarRef(blkTemp) { Type = blkTemp.Type }) { Type = rt.ResultType });
            });
        }
        var value = rt.ResultType is { } sk ? LowerExprSink(valueItem, sk) : LowerExpr(valueItem);
        rt.ResultType ??= value.Type;
        return new ExprStmt(new Assign(null, RtRef(rt), value) { Type = rt.ResultType });
    }
    /// <summary>An lvalue reference to a value-control-flow result temp at its (now-resolved) type.
    /// Only called after a branch has fixed <see cref="ValueTempTarget.ResultType"/>, so it is non-null.</summary>
    private static VarRef RtRef(ValueTempTarget rt) => new VarRef(rt.Temp) { Type = rt.ResultType!, IsLValue = true };
    /// <summary>Build the statement <c>switch</c> that fills a value-control-flow result temp — each
    /// prong assigns the temp (via <see cref="FillValueTemp"/>) then <c>break</c>s (Zig has no
    /// fall-through). Because it's a STATEMENT switch over a default-initialized temp, C#'s
    /// switch-expression exhaustiveness rule (CS8509) doesn't apply. (Milestone Y, part 1.) A
    /// tagged-union value-switch (tag dispatch + payload capture in value position) and a void block
    /// prong / <c>|x|</c> capture in a switch expression stay clear deferred errors.</summary>
    private CStmt BuildValueSwitch(Item subjectItem, Item prongsItem, ValueTempTarget rt)
        => WithSwitchBarrier(() => BuildValueSwitchCore(subjectItem, prongsItem, rt));
    private CStmt BuildValueSwitchCore(Item subjectItem, Item prongsItem, ValueTempTarget rt)
    {
        // A COMPTIME subject (road-to-zig-std S5) fills the result temp from the one selected prong —
        // the statement-context sibling of the fold in LowerSwitchExpr, reached when a prong needs
        // statements to produce its value (a block body / a labeled `break :blk v`).
        if (SelectComptimeProng(subjectItem, prongsItem, out var ctPayload) is { } ctProng)
        {
            // A selected BLOCK prong (`.comptime_int => comptime { …; return result; }` in std.math.log2) runs in
            // place: its `return` leaves the function, so the result temp is never read.
            if (ctProng.Expr is null && ctProng.Block is { } ctBlock)
            {
                EnterComptimeProng(ctProng, ctPayload);
                try
                {
                    rt.ResultType ??= CType.Int;
                    return LowerBlock(ctBlock);
                }
                finally { ExitComptimeProng(); }
            }
            if (ctProng.Expr is not { } ctValue)
            {
                throw new IrUnsupportedException(
                    "zig `switch (@typeInfo(T))` in value position: the selected prong must yield a value "
                    + "(`.int => expr` or `.int => blk: {… break :blk v;}`)");
            }
            EnterComptimeProng(ctProng, ctPayload);
            try { return FillValueTemp(ctValue, rt); }
            finally { ExitComptimeProng(); }
        }
        var subject = LowerExpr(subjectItem);
        var uname = subject.Type.Unqualified switch
        {
            CType.Named nm => nm.Name,
            CType.Pointer { Pointee: var pe } when pe.Unqualified is CType.Named pn => pn.Name,
            _ => null,
        };
        if (uname is not null && _unions.TryGetValue(uname, out var valueUnion))
        {
            return LowerUnionSwitch(subject, prongsItem, valueUnion, item => FillValueTemp(item, rt));
        }
        // A `|x|` prong capture of a plain (non-union) subject binds the subject's own value — the
        // `else => |e| return e` idiom that ends most `catch |err| switch (err) {…}` blocks in std. The
        // subject is read once into a temp when a capture needs it again (it may be a call).
        var pre = new List<CStmt>();
        var prongs = Flatten(prongsItem);
        if (subject is not VarRef && prongs.Any(p => DecomposeProng(p) is { CaptureName: not null, ByRef: false, Block: null }))
        {
            var st = _symbols.Declare(new Symbol { Name = "__sw" + _blockLabelCounter++, Kind = SymKind.Var, Type = subject.Type });
            pre.Add(new DeclStmt(new List<LocalDecl> { new(st, subject) }));
            subject = new VarRef(st) { Type = subject.Type, IsLValue = true };
        }
        var sections = new List<SwitchSection>();
        foreach (var prongItem in prongs)
        {
            // Each prong either YIELDS the value (fill the temp) or JUMPS (`=> return v`), which is how
            // a value switch reports the cases it cannot answer — the prong never reaches the consumer.
            var view = DecomposeProng(prongItem);
            var (caseVals, capture) = (view.CaseVals, view.CaptureName);
            Func<CStmt> body = prongItem.Content is Zig.InlineProng || view.ByRef || view.Comptime ? ThrowValueProng
                : view switch
                {
                    { Expr: { } value } => () => FillValueTemp(value, rt),
                    { Return: { } returned } => () => LowerReturn(returned),
                    { ReturnsVoid: true } => () => LowerReturnVoid(),
                    { Jump: { } jump } => () => LowerProngJump(jump),
                    // A block that always jumps (`error.OutOfMemory => { return; }` in std.PriorityQueue's `ensureTotalCapacity`,
                    // task #103) is `noreturn`, which a value switch accepts like `=> return`.
                    { Block: { } block } => () => LowerNoReturnProngBlock(block),
                    _ => ThrowValueProng,
                };
            static CStmt ThrowValueProng() => throw new IrUnsupportedException(
                "a value-position switch prong must yield a value (`v => expr` or `v => blk: {… break :blk v;}`) "
                + "or jump (`v => return …`); a void block prong or a `|*x|` capture in a switch expression is not supported yet");
            var labels = LowerCaseVals(caseVals, subject.Type);
            var stmts = new List<CStmt>();
            _symbols.EnterScope();
            try
            {
                if (capture is { } cap && cap != "_")
                {
                    var capSym = _symbols.Declare(new Symbol { Name = cap, Kind = SymKind.Var, Type = subject.Type });
                    stmts.Add(new DeclStmt(new List<LocalDecl> { new(capSym, subject) }));
                }
                var stmt = body();
                stmts.Add(stmt);
                // A jump needs no `break` (and an unreachable `break` after one is a C# warning).
                if (!Terminates(stmt)) { stmts.Add(new Break()); }
            }
            finally
            {
                _symbols.ExitScope();
            }
            sections.Add(new SwitchSection(labels, new List<CStmt> { new Block(stmts) }));
        }
        var sw = new Switch(subject, sections);
        if (pre.Count == 0) { return sw; }
        pre.Add(sw);
        return new Seq(pre);
    }
    /// <summary>Lower a value-position switch prong's BLOCK, which must never complete (every path ends in a
    /// <c>return</c>, <c>break</c>, <c>continue</c> or <c>unreachable</c>): a block that falls through would yield
    /// <c>void</c> where the switch needs a value, which zig rejects too.</summary>
    private CStmt LowerNoReturnProngBlock(Item block)
    {
        var lowered = LowerBlock(block);
        if (!Terminates(lowered))
        {
            throw new IrUnsupportedException(
                "a value-position switch prong must yield a value (`v => expr` or `v => blk: {… break :blk v;}`) "
                + "or jump (`v => return …`); a void block prong or a `|*x|` capture in a switch expression is not supported yet");
        }
        return lowered;
    }
    /// <summary>True when a lowered statement list provably ends control flow (so no
    /// synthetic <see cref="Break"/> is needed for a switch section). Mirrors the C# backend's
    /// own <c>Terminates</c>.</summary>
    private static bool EndsInJump(IReadOnlyList<CStmt> body) =>
        body.Count > 0 && Terminates(body[^1]);
    private static bool Terminates(CStmt s) => s switch
    {
        Return or Break or Continue or Goto => true,
        ExprStmt { Expr: Call { Callee: "__dotcc_unreachable" } } => true,   // `unreachable` lowers to a throw
        Block b => b.Stmts.Count > 0 && Terminates(b.Stmts[^1]),
        // A hoisted return (`return math.powi(T, x, y) catch unreachable;` in std.math.pow): its temps come first.
        Seq q => q.Stmts.Count > 0 && Terminates(q.Stmts[^1]),
        If f => f.Else is { } e && Terminates(f.Then) && Terminates(e),
        _ => false,
    };
    /// <summary>Lower <c>return e;</c>. In a <c>!T</c> function the value becomes an error
    /// union: <c>return error.Foo;</c> → an <see cref="ErrUnionErr"/>; a value that is ALREADY
    /// an error union (<c>return f();</c> where <c>f</c> returns <c>!U</c>) is returned as-is
    /// (Zig doesn't auto-unwrap); any plain value is wrapped in an <see cref="ErrUnionOk"/>.
    /// Outside an error-union function it is a plain <see cref="Return"/>.</summary>
    /// <summary>True when a call lowers to a plain <c>void</c> (probed under a throwaway hoist).</summary>
    private bool IsVoidCall(Item call)
    {
        using var _ = EnterThrowawayHoist();
        return LowerExpr(call).Type.Unqualified is CType.VoidType;
    }
    /// <summary>A returned switch or <c>if</c> whose arms mix error unions or error values with plain values
    /// (std.unicode's <c>return switch (bytes.len) { 1 =&gt; bytes[0], 2 =&gt; utf8Decode2(…), … }</c>,
    /// <c>utf8ByteSequenceLength</c>'s <c>else =&gt; error.Utf8InvalidStartByte</c>, <c>return if (ok) v else error.E;</c>):
    /// each plain arm becomes the success of <paramref name="eu"/> and an error value its failure, so the expression is
    /// the error union itself. Wrapping the whole of it as a success had returned an error's code as the payload,
    /// silently. Nested arms unify the same way. Null when no arm is an error union or error value.</summary>
    private static CExpr? UnifyErrUnionArms(CExpr value, CType.ErrorUnion eu)
    {
        var errorIsPayload = eu.Payload.Unqualified is CType.ErrorSetType;
        bool IsError(CExpr v) => v.Type?.Unqualified is CType.ErrorSetType && !errorIsPayload;
        bool NeedsUnify(CExpr v) => Unparen(v) switch
        {
            SwitchExpr s => s.Arms.Any(a => NeedsUnify(a.Value)),
            CondExpr c => NeedsUnify(c.Then) || NeedsUnify(c.Else),
            var leaf => leaf.Type?.Unqualified is CType.ErrorUnion || IsError(leaf),
        };
        CExpr Arm(CExpr v) => Unparen(v) switch
        {
            SwitchExpr s => s with { Arms = s.Arms.Select(a => a with { Value = Arm(a.Value) }).ToList(), Type = eu },
            CondExpr c => c with { Then = Arm(c.Then), Else = Arm(c.Else), Type = eu },
            var leaf when leaf.Type?.Unqualified is CType.ErrorUnion || leaf is Call { Callee: "__dotcc_unreachable" } => leaf,
            var leaf when IsError(leaf) => new ErrUnionErr(leaf) { Type = eu },
            var leaf => new ErrUnionOk(leaf) { Type = eu },
        };
        return Unparen(value) is SwitchExpr or CondExpr && NeedsUnify(value) ? Arm(value) : null;
    }
}
