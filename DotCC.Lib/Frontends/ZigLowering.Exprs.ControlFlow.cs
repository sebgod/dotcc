#nullable enable

using System.Collections.Generic;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Expressions, control flow: value <c>if</c>/<c>switch</c>, labeled blocks and <c>orelse</c>/<c>catch</c>.
/// One concern of the <see cref="ZigLowering"/> binder; class doc + shared state live
/// in the main file.</summary>
internal sealed partial class ZigLowering
{
    /// <summary>Lowers value-position control flow: empty switch, parenthesized constructs, <c>if</c>, <c>switch</c>, labeled blocks, <c>orelse</c>/<c>catch</c> and the rejected jumps.</summary>
    private CExpr LowerControlFlowExpr(Item expr)
    {
        switch (expr.Content)
        {
            // A switch with no prongs (std's unicode.zig, over an empty error set) has no value to reach: it is `unreachable`.
            case Zig.SwitchExprEmpty: return UnreachableCall();
            case Zig.Grouped g:
            {
                // A parenthesized value-position control-flow construct — `( if … )` / `( switch … )` /
                // `( blk: {…} )` / `( while/for … else … )` — used in a SUB-expression (Phase B of the
                // ANF milestone). It lowers to a STATEMENT filling a result temp (LowerValueIfSwitch /
                // LowerLabeledValueBlock / LowerLoopValue), so hoist that statement to the buffer and
                // evaluate to the temp. The sink is unknown here (the paren's use decides it), so the
                // temp type is inferred from the first branch/break value.
                if (IsValueControlFlowStmt(g.Arg1) || IsLabeledValue(g.Arg1))
                {
                    var savedImpure = _hoistImpureSeen;
                    Symbol? captured = null;
                    // The consumer captures the result temp and contributes nothing (an empty Seq) — the
                    // whole temp-filling statement goes to the buffer and the paren evaluates to the temp.
                    Func<Symbol, CStmt> cap = sym => { captured = sym; return new Seq(new List<CStmt>()); };
                    CStmt filled = IsLabeledValue(g.Arg1)
                        ? LowerLabeledValue(g.Arg1, null, cap)
                        : LowerValueControlFlowStmt(g.Arg1, null, cap);
                    _hoistImpureSeen = savedImpure;   // internals are sequenced in the buffer
                    var buf = RequireHoistable("value if/switch/block/loop in a sub-expression");
                    if (captured is not { } vsym)
                    {
                        throw new IrUnsupportedException("internal: value control-flow did not bind a result temp");
                    }
                    buf.Add(filled);
                    return new VarRef(vsym) { Type = vsym.Type };
                }
                var inner = LowerExpr(g.Arg1);
                return new Paren(inner) { Type = inner.Type };
            }

            // A value `if` with one arm that returns or jumps (`if (c) return v else w`, `x else break`): the jump hoists
            // ahead of the statement, and the expression is the other arm's value.
            case Zig.IfElseExpr { Arg4: null, Arg7: null } ir when ir.Arg5.Content is Zig.ReturnExpr { Arg1: { } thenReturned }:
                return LowerIfReturnThen(ir.Arg2, thenReturned, ir.Arg8, null);
            case Zig.IfElseExpr { Arg4: null, Arg7: null } er when er.Arg8.Content is Zig.ReturnExpr { Arg1: { } elseReturned }:
                return LowerIfElseReturn(er.Arg2, er.Arg5, elseReturned, null);
            case Zig.IfElseExpr { Arg4: null, Arg7: null } tj when IsJumpArm(tj.Arg5):
                return LowerIfEarlyJump(tj.Arg2, jumpOnTrue: true, tj.Arg5, tj.Arg8, null);
            case Zig.IfElseExpr { Arg4: null, Arg7: null } ej when IsJumpArm(ej.Arg8):
                return LowerIfEarlyJump(ej.Arg2, jumpOnTrue: false, ej.Arg8, ej.Arg5, null);
            // if (cond) a else b  — the if-EXPRESSION, lowered to a ternary. Both
            // branches are RhsExpr; the backend renders the condition as a bool.
            case Zig.IfElseExpr { Arg4: null, Arg7: null } e:
            {
                // A comptime condition (a TYPE comparison `Result == Accumulate` in std.fmt.parseIntWithSign,
                // a comptime tag) selects its arm at lowering time, as a statement `if`'s does: the other arm
                // may not even lower (it may name a value only the taken arm's types admit).
                if (TryFoldComptimeCondition(e.Arg2) is { } taken) { return LowerExpr(taken ? e.Arg5 : e.Arg8); }
                // The condition first, as zig evaluates it; each arm under its own hoist buffer (task #203).
                var ifImpure = _hoistImpureSeen;
                var ifCond = LowerExpr(e.Arg2);
                var (then, thenPre) = LowerArmIsolated(() => LowerExpr(e.Arg5));
                var (otherwise, elsePre) = LowerArmIsolated(() => LowerExpr(e.Arg8));
                if (thenPre.Count > 0 || elsePre.Count > 0)
                {
                    return HoistedValueIf(ifCond, ifImpure, then, thenPre, otherwise, elsePre,
                        HoistedResultType(null, then.Type, new[] { then, otherwise }));
                }
                return new CondExpr(ifCond, then, otherwise) { Type = then.Type };
            }
            // Value-position captured `if` — `if (opt) |x| thenE else elseE` (S4a). The payload binds
            // `x` in the then-branch, so a pure ternary can't express it; it hoists (ANF) to a result
            // temp assigned by a real `if`. See LowerIfCaptureExpr.
            case Zig.IfElseExpr ec:
                return LowerIfCaptureExprOf(ec, null);
            // A switch EXPRESSION reached with no result-location type (e.g. `x = switch(y){…}`
            // where the LHS type still flows in via LowerExprSink, or an inferred `const`). The
            // sink-carrying path is in LowerExprSink; here the arm types are inferred.
            case Zig.SwitchExpr s:         return LowerSwitchExpr(s.Arg2, s.Arg5, null);

            // A labeled value-block in a pure-expression position (an if/switch-expression arm, a
            // binary sub-operand) — it produces a value via statements, which a C# expression can't
            // host, so it's supported only as a full `=` / `return` / assignment RHS (intercepted in
            // DeclOf / LowerReturn / StmtAssign before reaching here). A clear deferred error.
            case Zig.LabeledBlock or Zig.LabeledSwitch when _hoist is not null:
                return HoistLabeledValue(expr, null);
            case Zig.LabeledBlock or Zig.LabeledSwitch:
                throw new IrUnsupportedException(
                    $"a labeled value-block (`{Tok(expr.Content is Zig.LabeledBlock lbl ? lbl.Arg0 : ((Zig.LabeledSwitch)expr.Content).Arg0)}: {{ … }}`) is supported only as a full initializer, " +
                    "`return`, or assignment right-hand side (including as a value-position if/switch branch there, " +
                    "Milestone Y part 1) — not inside a sub-expression yet");

            // `a orelse b`. A value optional → C#'s `??` (single-eval LHS, lazy RHS) via
            // NullCoalesce. An optional POINTER → `a != null ? a : b` (C# `??` doesn't apply
            // to pointers); the LHS is named twice there, so a non-trivial (side-effecting)
            // left operand is rejected rather than silently double-evaluated. `orelse return`
            // (a noreturn RHS) isn't expressible in the grammar yet — that's Milestone B2.
            // A control-flow `orelse` / `catch` (its fallback is noreturn): see LowerControlFlowFallbackExpr.
            case Zig.OrElse or Zig.CatchOp or Zig.CatchCapture when IsControlFlowFallback(expr, out _, out _, out _, out _):
                return LowerControlFlowFallbackExpr(expr);
            case Zig.OrElse o:
            {
                var impureBeforeLeft = _hoistImpureSeen;
                var left = LowerExpr(o.Arg0);
                // A comptime-known optional (`comptime st.nextArg(null) orelse @compileError(…)`, std.fmt): its
                // payload, or the fallback when it is null; an untaken fallback is never lowered.
                if (left is ComptimeFold { Resolved: { } known } && left.Type.Unqualified is CType.Optional)
                {
                    return known is DefaultLit ? LowerExpr(o.Arg2) : known;
                }
                // A comptime-only `?comptime_int` call (`std.simd.suggestVectorLength(u8) orelse 0`) may already be its
                // folded value: a payload literal, or `null` as a default.
                var foldedOptional = left is ComptimeFold { Resolved: { } fk } ? fk : left;
                // The null is a `DefaultLit` of the optional type (a user `?comptime_int` function, task #149).
                if ((left.Type.Unqualified is not CType.Optional and not CType.Pointer || foldedOptional is DefaultLit)
                    && foldedOptional is LitInt or DefaultLit or Cast { Operand: LitInt }
                    && ReturnsOptionalComptimeInt(o.Arg0.Content is Zig.PreComptime lpc ? lpc.Arg1 : o.Arg0))
                {
                    return foldedOptional is DefaultLit ? LowerExpr(o.Arg2) : foldedOptional;
                }
                // `opt orelse error.E` is an ERROR UNION (zig peer-resolves the payload with the error): the payload when
                // there is one, else the error. Lowered at the payload type instead, the error's flat CODE was the value
                // (std.fmt.parseFloat's `parseInfOrNan(…) orelse error.InvalidCharacter` returned 10.0 for "abc").
                if (o.Arg2.Content is Zig.ErrorLit orErr && left.Type.Unqualified is CType.Optional { Inner: var orPayload })
                {
                    var unionType = new CType.ErrorUnion(orPayload);
                    return new Call("ErrUnion.OrError", new List<CExpr> { left, LowerErrorLit(Tok(orErr.Arg2)) }) { Type = unionType };
                }
                // An optional POINTER or ARRAY lowers to a conditional that reads the left operand twice, so one that is not
                // a pure reread (`get(1) orelse &fallback`, a call) is bound to a temp first where the statement can hoist it
                // (task #193). The fallback is lowered after, so any hoist of its own is sequenced behind the temp.
                if (left.Type.Unqualified is CType.Pointer or CType.Optional { Inner.Unqualified: CType.Array }
                    && !IsPureReread(left) && _hoist is not null && !impureBeforeLeft)
                {
                    left = HoistLowered("orelse", new List<CStmt>(), left, impureBeforeLeft);
                }
                // The fallback is at the payload's result type (`alignment orelse default_alignment`, an enum literal). It
                // runs only when the left is null, so what it hoists stays with it (task #203).
                var (right, rightHoisted) = LowerArmIsolated(() => left.Type.Unqualified is CType.Optional { Inner: var fallbackSink }
                    ? LowerExprSink(o.Arg2, fallbackSink)
                    : LowerExpr(o.Arg2));
                if (rightHoisted.Count > 0) { return OrElseHoistedFallback(left, impureBeforeLeft, right, rightHoisted); }
                // An optional ARRAY (task #151) is a generated value type, which C#'s `??` does not apply to: its payload
                // (the element pointer) when it has one, else the fallback array.
                if (left.Type.Unqualified is CType.Optional { Inner.Unqualified: CType.Array } optArray)
                {
                    if (!IsPureReread(left))
                    {
                        throw new IrUnsupportedException(
                            "zig `orelse` on an optional array with a non-trivial left operand not lowered yet (it would be double-evaluated)");
                    }
                    var hasValue = new Member(left, "HasValue", false) { Type = CType.Bool };
                    return new CondExpr(hasValue, new Member(left, "Value", false) { Type = optArray.Inner }, right) { Type = optArray.Inner };
                }
                if (left.Type.Unqualified is CType.Optional opt)
                {
                    return new NullCoalesce(left, right) { Type = opt.Inner };
                }
                if (left.Type.Unqualified is CType.Pointer)
                {
                    if (!IsPureReread(left))
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
            // propagation). A simple, side-effect-free `b` keeps the eager `ErrUnion.Catch(a, b)`
            // (C# evaluates `b` before the call, which is observationally identical to Zig's lazy
            // form only when `b` has no side effects). A side-effecting `b` (Milestone N, part 3)
            // needs a LAZY lowering that runs `b` only on error, which requires a statement context
            // (a `const`/`var` initializer — see DeclOf / LowerCatchValue); in a sub-expression
            // position only the eager form is available.
            case Zig.CatchOp c:
            {
                // Lower ONCE (snapshot the impurity watermark first — the union operand is a call that
                // sets it). A simple, side-effect-free fallback → the eager `ErrUnion.Catch` (no pre,
                // the union call legitimately counts as a side effect). A side-effecting fallback →
                // pre-statements; hoist them to a temp before the enclosing statement (the ANF pass).
                var savedC = _hoistImpureSeen;
                var (pre, value) = LowerCatchValue(c.Arg0, null, c.Arg2);
                if (pre.Count == 0) { return value; }
                return HoistLowered("catch", pre, value, savedC);
            }
            // `a catch |e| b` (Milestone N, part 3) — bind the error to `e` for the fallback `b`,
            // evaluated lazily (only on error). The bind is a statement, so hoist it (ANF).
            case Zig.CatchCapture cc:
            {
                var savedCc = _hoistImpureSeen;
                var (pre, value) = LowerCatchValue(cc.Arg0, Tok(cc.Arg3), cc.Arg5);
                return HoistLowered("catch |e|", pre, value, savedCc);
            }

            // `a catch return [x]` / `a orelse return [x]` (control-flow fallback) — the `return` is a
            // statement. In a full-RHS position DeclOf/LowerStmt handle it; in a SUB-expression it is
            // hoisted to a temp before the enclosing statement (ANF): the conditional `return` and the
            // payload capture become buffer statements, and the construct evaluates to the payload temp.
            // The statement-shaped arms (road-to-zig-std) — `orelse break`, `catch |err| switch (err) {…}`,
            // `orelse { …; return; }` — take exactly the same hoist: a conditional arm, then the payload.
            case Zig.OrElseArm or Zig.CatchArm or Zig.CatchCaptureArm:
                return LowerControlFlowFallbackExpr(expr);
            // A jump in a position no construct above gives it (`f(return 1)`, `x + break`): zig accepts it, as a noreturn
            // operand, but nothing would run after it, so dotcc has no lowering for it.
            case Zig.ReturnExpr or Zig.FbBreak or Zig.FbBreakLabel or Zig.FbBreakLabelValue or Zig.FbBreakValue
              or Zig.FbContinue or Zig.FbContinueLabel or Zig.FbContinueLabelValue:
                throw new IrUnsupportedException(
                    "zig: a `return` / `break` / `continue` is lowered as an `orelse` / `catch` fallback, an `if` arm or a statement, "
                    + "not as an operand here");

            default: throw new IrUnsupportedException("zig expression: " + (expr.Content?.GetType().Name ?? "null"));
        }
    }
}
