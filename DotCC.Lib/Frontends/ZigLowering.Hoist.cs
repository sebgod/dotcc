#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>The ANF statement hoist: a construct that lowers to statements, in a sub-expression, appends them to a
/// per-statement buffer and evaluates to a temp, guarded against reordering an earlier side effect. Also the struct
/// literal fields held back for a later field that hoists. One concern of the <see cref="ZigLowering"/>
/// binder.</summary>
internal sealed partial class ZigLowering
{
    // ---- ANF statement-hoist (the "sub-expression positions" milestone) --------------------------
    //
    // A value-producing construct that lowers to STATEMENTS (a side-effecting/capturing `catch`, a
    // `catch return` / `orelse return`) works at a full RHS (const/var/return/assignment) but not in
    // a SUB-expression (`x + (a catch b())`). The ANF hoist lifts it to a temp before the enclosing
    // statement: `Hoisted` installs a per-statement buffer at each eval-safe point, and the construct
    // appends its pre-statements + a result temp and evaluates to a bare VarRef. Correctness rides on
    // `_hoistImpureSeen`: hoisting past an earlier side effect would reorder it, so that is rejected.

    /// <summary>Lower a statement (via <paramref name="lower"/>) under a fresh ANF hoist buffer, then
    /// prepend any hoisted statements as a brace-less <see cref="Seq"/> (the result temps stay in the
    /// enclosing block scope). A statement with no hoist returns unchanged. Installed only at
    /// eval-safe statement points — NOT a loop condition (re-evaluated per iteration).</summary>
    private CStmt Hoisted(Func<CStmt> lower)
    {
        using var _ = EnterFreshHoist();
        var stmt = lower();
        if (_hoist is not { Count: > 0 } hoisted) { return stmt; }
        return new Seq(new List<CStmt>(hoisted) { stmt });
    }
    /// <summary>Guard + finish a sub-expression hoist: reject when not in a hoistable position
    /// (<see cref="_hoist"/> null) or when a side effect was already evaluated earlier in the
    /// statement (<see cref="_hoistImpureSeen"/> — hoisting past it would reorder). Otherwise lower
    /// the construct (its own internals don't count toward a LATER hoist — restore the flag), append
    /// its <paramref name="pre"/>-computing statements + a <c>__anfN</c> result temp to the buffer,
    /// and return a bare <see cref="VarRef"/> to that temp.</summary>
    private CExpr HoistLowered(string what, List<CStmt> pre, CExpr value, bool savedImpure)
    {
        // Restore the impurity watermark to its PRE-construct value: the construct's own internals
        // (lowered by the caller) are sequenced into the buffer, so they don't block a LATER sibling
        // hoist. RequireHoistable then rejects only a reordering hazard against a PRIOR side effect.
        _hoistImpureSeen = savedImpure;
        var buf = RequireHoistable(what);
        // A VOID value (`self.resize(a, 0, false) catch unreachable;` over a `!void`, std.bit_set's deinit, task #130) has
        // nothing to bind: it runs as a statement, and the construct is the void value.
        if (value.Type.Unqualified is CType.VoidType)
        {
            buf.AddRange(pre);
            buf.Add(new ExprStmt(value));
            return new DefaultLit { Type = CType.Void };
        }
        var sym = _symbols.Declare(new Symbol { Name = "__anf" + _anfTempCounter++, Kind = SymKind.Var, Type = value.Type });
        buf.AddRange(pre);
        buf.Add(new DeclStmt(new List<LocalDecl> { new(sym, value) }));
        return new VarRef(sym) { Type = value.Type };
    }
    /// <summary>Return the active hoist buffer, or throw a clear error when a statement-lowering
    /// construct appears where it can't be hoisted: no active buffer (e.g. a loop condition), or
    /// after an earlier side effect in the same statement (a reordering hazard — bind to a
    /// <c>const</c> first). Returning the (non-null) buffer avoids a null-forgiving deref at the
    /// call site.</summary>
    private List<CStmt> RequireHoistable(string what)
    {
        if (_hoist is not { } buf)
        {
            throw new IrUnsupportedException(
                $"zig `{what}` is lowered as a `const`/`var` initializer, `return`, assignment, or expression statement — this position (e.g. a loop condition) isn't hoistable; bind it to a `const` first");
        }
        if (_hoistImpureSeen)
        {
            throw new IrUnsupportedException(
                $"zig `{what}` in a sub-expression can't be hoisted past an earlier side-effecting operand in the same statement — bind it to a `const` first");
        }
        // An earlier field of an enclosing struct literal, held back (task #195), goes into the buffer first.
        SpillHeldSiblings(buf);
        return buf;
    }

    /// <summary>A struct literal's fields already lowered, while a LATER field lowers (task #195). zig evaluates the fields
    /// in order, so a later field that must hoist a statement (std.SemanticVersion.parse's
    /// <c>.minor = try parseNum(it.next() orelse return error.InvalidVersion)</c>, after
    /// <c>.major = try parseNum(it.first())</c>) cannot be hoisted past an earlier field's call. Instead that earlier
    /// field goes first: it is bound to a temp in the same buffer, just ahead of the hoisted statement. While a later
    /// field lowers, the earlier fields' impurity is held in <see cref="ImpureUnspilled"/> rather than in the watermark,
    /// and <see cref="RequireHoistable"/> spills it on demand. When no hoist comes nothing is spilled, and the literal
    /// emits as before.</summary>
    private sealed class SiblingSpillFrame(SiblingSpillFrame? parent, List<CStmt> buffer, List<FieldInit> members)
    {
        /// <summary>The enclosing literal's frame, when this literal is one of its fields.</summary>
        internal SiblingSpillFrame? Parent { get; } = parent;

        /// <summary>The hoist buffer the literal is lowered against; only a hoist into this same buffer spills.</summary>
        internal List<CStmt> Buffer { get; } = buffer;

        /// <summary>The literal's lowered fields, in order; a spilled one's value is replaced by its temp.</summary>
        internal List<FieldInit> Members { get; } = members;

        /// <summary>Indices into <see cref="Members"/> of the fields with a side effect that is not in the buffer yet.</summary>
        internal List<int> ImpureUnspilled { get; } = [];
    }
    /// <summary>Bind every held-back impure field (<see cref="SiblingSpillFrame"/>) of the literals being lowered against
    /// <paramref name="buffer"/> to a temp in it, outermost literal first, since an enclosing literal's earlier fields
    /// were evaluated before this one's. Called just before a construct appends its hoisted statements.</summary>
    private void SpillHeldSiblings(List<CStmt> buffer)
    {
        var chain = new List<SiblingSpillFrame>();
        for (var f = _siblingSpill; f is not null && ReferenceEquals(f.Buffer, buffer); f = f.Parent) { chain.Add(f); }
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var frame = chain[i];
            foreach (var index in frame.ImpureUnspilled)
            {
                var member = frame.Members[index];
                if (member.Value.Type.Unqualified is CType.VoidType)
                {
                    buffer.Add(new ExprStmt(member.Value));
                    frame.Members[index] = member with { Value = new DefaultLit { Type = CType.Void } };
                    continue;
                }
                var temp = _symbols.Declare(new Symbol { Name = "__anf" + _anfTempCounter++, Kind = SymKind.Var, Type = member.Value.Type });
                buffer.Add(new DeclStmt(new List<LocalDecl> { new(temp, member.Value) }));
                frame.Members[index] = member with { Value = new VarRef(temp) { Type = member.Value.Type } };
            }
            frame.ImpureUnspilled.Clear();
        }
    }

    /// <summary>Lower one arm of a value <c>if</c> under its own hoist buffer (task #203): a statement the arm hoists (the
    /// early return of <c>c and (opt orelse return false)</c>, a labeled block) runs only when the arm is taken, so it must not join the enclosing statement's buffer, which runs whichever way the test goes.
    /// <paramref name="lowerArm"/> lowers the arm; returns its value and what it hoisted (empty when nothing).</summary>
    private (CExpr Value, List<CStmt> Hoisted) LowerArmIsolated(Func<CExpr> lowerArm)
    {
        // Outside a hoistable position there is no buffer to keep the arm's statements out of: an arm that needs one is
        // rejected there, as before.
        if (_hoist is null) { return (lowerArm(), new List<CStmt>()); }
        using (EnterFreshHoist())
        {
            var value = lowerArm();
            return (value, _hoist is { Count: > 0 } armHoisted ? new List<CStmt>(armHoisted) : new List<CStmt>());
        }
    }

    /// <summary>The type of the result temp of a value <c>if</c> / switch whose arms hoisted: an arithmetic
    /// <paramref name="sink"/> when there is one, else the arithmetic peer of the arms (the ternary it replaces was typed
    /// by C# the same way: <c>c ? 5 : someULong</c> is a <c>ulong</c>, and an <c>int</c> temp would not hold it). A
    /// <c>bool</c> is not widened, and anything else keeps <paramref name="fallback"/>.</summary>
    private static CType HoistedResultType(CType? sink, CType fallback, IEnumerable<CExpr> arms)
    {
        if (sink is { IsArithmetic: true } arithmeticSink && arithmeticSink.Unqualified != CType.Bool) { return arithmeticSink; }
        CType? peer = null;
        foreach (var arm in arms)
        {
            if (IsUnreachableCallExpr(arm)) { continue; }
            if (peer is null) { peer = arm.Type; continue; }
            if (peer.Unqualified is not CType.Prim p || arm.Type.Unqualified is not CType.Prim a || p == a
                || p == CType.Bool || a == CType.Bool)
            {
                return fallback;
            }
            peer = CType.UsualArithmetic(peer, arm.Type);
        }
        return peer is { Unqualified: CType.Prim } ? peer : fallback;
    }

    /// <summary>A value <c>if</c> an arm of which hoisted statements (<see cref="LowerArmIsolated"/>): no longer a ternary,
    /// but a statement <c>if</c> hoisted ahead of the enclosing statement, each branch running its arm's statements and
    /// filling one result temp, which is the value. <paramref name="savedImpure"/> is the watermark before the condition
    /// was lowered: the condition and both arms are sequenced into the buffer with the <c>if</c>.</summary>
    private CExpr HoistedValueIf(CExpr cond, bool savedImpure, CExpr then, List<CStmt> thenPre, CExpr otherwise,
        List<CStmt> elsePre, CType type)
    {
        _hoistImpureSeen = savedImpure;
        var buf = RequireHoistable("value `if` whose arm needs a statement");
        if (type.Unqualified is CType.VoidType)
        {
            thenPre.Add(new ExprStmt(then));
            elsePre.Add(new ExprStmt(otherwise));
            buf.Add(new If(cond, new Block(thenPre), new Block(elsePre)));
            return new DefaultLit { Type = CType.Void };
        }
        var temp = _symbols.Declare(new Symbol { Name = "__anf" + _anfTempCounter++, Kind = SymKind.Var, Type = type });
        var target = new VarRef(temp) { Type = type, IsLValue = true };
        // An `unreachable` arm has no value to store: it traps where it stands.
        thenPre.Add(IsUnreachableCallExpr(then) ? new ExprStmt(then) : new ExprStmt(new Assign(null, target, then) { Type = type }));
        elsePre.Add(IsUnreachableCallExpr(otherwise) ? new ExprStmt(otherwise) : new ExprStmt(new Assign(null, target, otherwise) { Type = type }));
        buf.Add(new DeclStmt(new List<LocalDecl> { new(temp, null) }));
        buf.Add(new If(cond, new Block(thenPre), new Block(elsePre)));
        return new VarRef(temp) { Type = type };
    }

    /// <summary><c>opt orelse b</c> whose fallback hoisted statements (task #203): the statement <c>if</c> of
    /// <see cref="HoistedValueIf"/> over the left operand's presence, read once (bound to a temp unless it is a pure
    /// reread). <paramref name="impureBeforeLeft"/> is the watermark before the left operand was lowered.</summary>
    private CExpr OrElseHoistedFallback(CExpr left, bool impureBeforeLeft, CExpr right, List<CStmt> rightHoisted)
    {
        _hoistImpureSeen = impureBeforeLeft;
        var buf = RequireHoistable("orelse whose fallback needs a statement");
        if (!IsPureReread(left))
        {
            var leftTemp = _symbols.Declare(new Symbol { Name = "__anf" + _anfTempCounter++, Kind = SymKind.Var, Type = left.Type });
            buf.Add(new DeclStmt(new List<LocalDecl> { new(leftTemp, left) }));
            left = new VarRef(leftTemp) { Type = left.Type };
        }
        if (left.Type.Unqualified is CType.Optional opt)
        {
            return HoistedValueIf(new Member(left, "HasValue", false) { Type = CType.Bool }, impureBeforeLeft,
                new Member(left, "Value", false) { Type = opt.Inner }, new List<CStmt>(), right, rightHoisted, opt.Inner);
        }
        if (left.Type.Unqualified is CType.Pointer)
        {
            var notNull = new Binary(BinOp.Ne, left, new NullPtr { Type = new CType.Pointer(CType.Void) }) { Type = CType.Int };
            return HoistedValueIf(notNull, impureBeforeLeft, left, new List<CStmt>(), right, rightHoisted, left.Type);
        }
        throw new IrUnsupportedException("zig `orelse` requires an optional left operand");
    }

    /// <summary>A switch expression an arm of which hoisted statements (<see cref="LowerArmIsolated"/>), the switch
    /// counterpart of <see cref="HoistedValueIf"/>: a statement switch hoisted ahead of the enclosing statement, each
    /// section running its arm's statements and filling one result temp. <paramref name="savedImpure"/> is the watermark
    /// before the subject was lowered.</summary>
    private CExpr HoistedValueSwitch(CExpr subject, bool savedImpure, IReadOnlyList<SwitchExprArm> arms,
        IReadOnlyList<List<CStmt>> armHoists, CType type)
    {
        _hoistImpureSeen = savedImpure;
        var buf = RequireHoistable("switch expression whose prong needs a statement");
        var isVoid = type.Unqualified is CType.VoidType;
        var temp = isVoid ? null : _symbols.Declare(new Symbol { Name = "__anf" + _anfTempCounter++, Kind = SymKind.Var, Type = type });
        var sections = new List<SwitchSection>();
        for (var i = 0; i < arms.Count; i++)
        {
            var body = new List<CStmt>(armHoists[i]);
            var value = arms[i].Value;
            // An `unreachable` arm (or a void result) has no value to store.
            body.Add(temp is null || IsUnreachableCallExpr(value)
                ? new ExprStmt(value)
                : new ExprStmt(new Assign(null, new VarRef(temp) { Type = type, IsLValue = true }, value) { Type = type }));
            if (!EndsInJump(body)) { body.Add(new Break()); }
            sections.Add(new SwitchSection(arms[i].Labels ?? new List<SwitchLabel> { new SwitchLabel(null) },
                new List<CStmt> { new Block(body) }));
        }
        if (temp is null)
        {
            buf.Add(new Switch(subject, sections));
            return new DefaultLit { Type = CType.Void };
        }
        buf.Add(new DeclStmt(new List<LocalDecl> { new(temp, null) }));
        buf.Add(new Switch(subject, sections));
        return new VarRef(temp) { Type = type };
    }
}
