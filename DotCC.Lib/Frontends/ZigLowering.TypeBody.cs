#nullable enable

using System.Collections.Generic;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>The body of a <c>type</c>-returning function, EVALUATED at comptime (the W4 lift —
/// road-to-zig-std G4 blocker 2, and the first brick on G3's road).
///
/// <para>W4 accepted exactly one body shape: <c>[const NAME = &lt;type&gt;;]* return struct {…};</c>.
/// Measured against the pinned std, that is 110 of ~250 type-returning bodies. The rest return a type
/// they did not declare: <b>28 delegate to another type-returning call</b>
/// (<c>pub fn ArrayList(comptime T: type) type { return array_list.Aligned(T, null); }</c> — which is
/// what <c>std.ArrayList</c> IS), 16 <c>return switch (@typeInfo(T)) {…}</c> (<c>std.meta.Child</c>),
/// a handful <c>return if (…) A else B</c> or <c>return @Int(…)</c>, and several open with a comptime
/// <c>if</c> that returns early (<c>array_list.Aligned</c>'s own
/// <c>if (alignment) |a| { if (…) return Aligned(T, null); }</c>).</para>
///
/// <para>So the body is now walked statement by statement, as comptime code: a leading type alias binds
/// (as before), an <c>if</c> whose condition folds contributes ONLY its taken arm, and the first
/// <c>return</c> reached ends the walk — either <c>return struct {…}</c> (reified exactly as W4 always
/// did) or <c>return &lt;type expression&gt;</c>, whose type IS the function's result. A delegating
/// result is not a new type: <c>Managed(u8)</c> and <c>AlignedManaged(u8, null)</c> are the same type in
/// zig, and here they resolve to the same reified <see cref="CType"/>, memoized under the delegating
/// instance's own mangled name so a repeat call does not re-walk the body.</para>
///
/// <para>The comptime-interpreter firewall stands (no <c>TypeVal</c>): all of this runs at the LOWERING
/// tier, over the same folds S4b/S5a/S3 built — <see cref="TryComptimeOptionalCond"/>,
/// <see cref="SelectComptimeProng"/>, <see cref="TryFoldComptimeCondition"/>. A condition or subject
/// none of them can settle is a loud cut, never a runtime branch: a type has no runtime.</para></summary>
internal sealed partial class ZigLowering
{
    /// <summary>What walking a type-returning body produced: either a <c>return struct {…}</c> to reify
    /// (<see cref="IsStruct"/>, with its <c>FieldDecls</c> item, null for <c>struct {}</c>) or an
    /// already-resolved type the body delegated to (<see cref="Delegated"/>) together with the declared
    /// integer width it carries (<see cref="DelegatedBits"/> — so <c>fn U() type { return u21; }</c>
    /// still answers 21, not the widened 32).</summary>
    private readonly record struct TypeBodyResult(bool IsStruct, Item? Fields, CType? Delegated, int? DelegatedBits);

    /// <summary>Mangled delegating instances → the type they resolved to, plus its declared width (the
    /// memo for a body that returns a type rather than a <c>struct {…}</c> — the struct form memoizes in
    /// <see cref="_containerTypes"/>). Keyed by the DELEGATING instance's name, so
    /// <c>Managed__u8</c> and <c>AlignedManaged__u8_optnull</c> both land on one reified struct.</summary>
    private readonly Dictionary<string, (CType Type, int? Bits)> _delegatedTypes = new(System.StringComparer.Ordinal);

    /// <summary>Instances whose body is being walked right now — the guard that turns a body delegating
    /// to ITSELF (<c>fn A(comptime T: type) type { return A(T); }</c>, which zig rejects as a dependency
    /// loop) into a loud error rather than a stack overflow. A struct body that refers to its own
    /// instance from a FIELD is unaffected: the memo is installed before fields lower.</summary>
    private readonly HashSet<string> _typeBodiesInProgress = new(System.StringComparer.Ordinal);

    /// <summary>The declared integer width each type-returning CALL SITE last resolved to, reference-keyed
    /// by the call node — the same "the site's recorded width" rule as <see cref="_reifiedIntBits"/>. Read
    /// by <see cref="DeclaredBitsOfTypeArg"/>, which every consumer calls AFTER lowering the same node, so
    /// the record is always the current instantiation's.</summary>
    private readonly Dictionary<Item, int> _typeCallBits = new(ReferenceEqualityComparer.Instance);

    /// <summary>Walk a type-returning generic's body at comptime (W4 + the W4 lift) and report what it
    /// returns. Leading <c>const NAME = &lt;type&gt;;</c> aliases are bound into <see cref="_typeAliases"/>
    /// (shadow-saved via <paramref name="typeShadows"/>, restored by the caller) so later statements and
    /// the returned struct's fields see them. A body that ends without reaching a <c>return</c> — every
    /// arm folded away — is a loud error, as it is in zig.</summary>
    private TypeBodyResult ProcessTypeReturningBody(string fnName, Item body, List<(string name, CType? prev, int? prevBits)> typeShadows)
    {
        var stmts = BodyStatements(body);
        if (stmts.Count == 0)
        {
            throw new IrUnsupportedException(
                $"type-returning generic '{fnName}': an empty body — expected `[const NAME = <type>;]* return <type>;`");
        }
        return WalkTypeBody(fnName, stmts, typeShadows)
            ?? throw new IrUnsupportedException(
                $"type-returning generic '{fnName}': the body reaches its end without returning a type "
                + "(every `return` sits in an arm that folded away)");
    }

    /// <summary>The statements of a body or an arm: a braced block's list, or the single statement.</summary>
    private static IReadOnlyList<Item> BodyStatements(Item stmt) => stmt.Content switch
    {
        Zig.Block b => Flatten(b.Arg1),
        Zig.BlockEmpty => System.Array.Empty<Item>(),
        _ => new[] { stmt },
    };

    /// <summary>Walk statements in order; the first <c>return</c> reached is the result. Null when the
    /// list ends without one (a folded-away early-return arm — the caller continues after it).</summary>
    private TypeBodyResult? WalkTypeBody(string fnName, IReadOnlyList<Item> stmts, List<(string name, CType? prev, int? prevBits)> typeShadows)
    {
        foreach (var stmt in stmts)
        {
            switch (stmt.Content)
            {
                // `const Slice = if (alignment) |a| … else []T;` — a TYPE alias; `const bits = @typeInfo(T).int.bits;`
                // — a comptime VALUE (std.math.Log2Int computes its result width from one). Which it is
                // is decided by the RHS's shape (IsTypeBodyTypeRhs), before anything is lowered.
                case Zig.ConstDecl cd when IsTypeBodyTypeRhs(cd.Arg3):
                {
                    var aliasName = Tok(cd.Arg1);
                    var (aliasType, aliasBits) = LowerComptimeTypeExpr(fnName, cd.Arg3);
                    typeShadows.Add((aliasName,
                                     _typeAliases.TryGetValue(aliasName, out var pv) ? pv : (CType?)null,
                                     _declaredIntBits.TryGetValue(aliasName, out var pb) ? pb : (int?)null));
                    _typeAliases[aliasName] = aliasType;
                    SetDeclaredIntBits(aliasName, aliasBits);
                    break;
                }
                case Zig.ConstDecl cv:
                    BindTypeBodyComptimeValue(fnName, Tok(cv.Arg1), null, cv.Arg3);
                    break;
                case Zig.ConstDeclTyped ct:   // `const bits: u16 = …;` — an annotated const is always a value
                    BindTypeBodyComptimeValue(fnName, Tok(ct.Arg1), ct.Arg3, ct.Arg5);
                    break;
                // `switch (@typeInfo(T)) { .array => |info| return info.child, …, else => {} }` as a STATEMENT
                // (std.meta.Elem): only the selected prong is walked, and a prong that falls through (`{}`)
                // continues after the switch.
                case Zig.StmtSwitch sw:
                    if (WalkComptimeSwitchStmt(fnName, sw.Arg2, sw.Arg5, typeShadows) is { } rs1) { return rs1; }
                    break;
                case Zig.StmtSwitchTrailing sw:
                    if (WalkComptimeSwitchStmt(fnName, sw.Arg2, sw.Arg5, typeShadows) is { } rs2) { return rs2; }
                    break;
                // `@compileError("…");` REACHED — every arm that avoids it has already folded away, which is
                // exactly zig's rule for it (std.meta.Elem ends with one after its switch).
                case Zig.StmtExpr { Arg0.Content: Zig.BuiltinCall ce } when Tok(ce.Arg0) == "@compileError":
                    CompileErrorBuiltin(Flatten(ce.Arg2));
                    break;
                // `_ = alignment;` — zig's unused-parameter silencer, which a type body needs as much as any
                // other (zig rejects an unused parameter). A bare NAME has nothing to evaluate; a discarded
                // CALL would, so it stays the loud cut below.
                case Zig.StmtAssign { Arg0.Content: Zig.Ident { } lhs, Arg2.Content: Zig.Ident } when Tok(lhs.Arg0) == "_":
                    break;
                case Zig.ReturnStructType rst:
                    return new TypeBodyResult(true, rst.Arg3, null, null);   // FieldDecls
                case Zig.ReturnStructTypeEmpty:
                    return new TypeBodyResult(true, null, null, null);       // `return struct {};` — zero fields
                case Zig.StmtReturn r:
                {
                    // `return <type expression>;` — the function's result IS that type (a delegating call,
                    // a folded `switch`/`if`, `@Int(…)`, a bare `T`). Its declared width comes back from the
                    // same fold, so it is the TAKEN arm's width.
                    var (t, bits) = LowerComptimeTypeExpr(fnName, r.Arg1);
                    return new TypeBodyResult(false, null, t, bits);
                }
                case Zig.StmtIf f:
                    if (WalkComptimeIf(fnName, f.Arg2, f.Arg4, null, typeShadows) is { } r1) { return r1; }
                    break;
                case Zig.StmtIfElse f:
                    if (WalkComptimeIf(fnName, f.Arg2, f.Arg4, f.Arg6, typeShadows) is { } r2) { return r2; }
                    break;
                case Zig.StmtIfCapture f:
                    if (WalkComptimeIfCapture(fnName, f.Arg2, Tok(f.Arg5), f.Arg7, null, typeShadows) is { } r3) { return r3; }
                    break;
                case Zig.StmtIfCaptureElse f:
                    if (WalkComptimeIfCapture(fnName, f.Arg2, Tok(f.Arg5), f.Arg7, f.Arg9, typeShadows) is { } r4) { return r4; }
                    break;
                case Zig.Block or Zig.BlockEmpty:
                    if (WalkTypeBody(fnName, BodyStatements(stmt), typeShadows) is { } r5) { return r5; }
                    break;
                default:
                    throw new IrUnsupportedException(
                        $"type-returning generic '{fnName}': a body statement must be a `const NAME = <type>;` alias, a "
                        + "comptime `if`, or a `return` — got " + (stmt.Content?.GetType().Name ?? "null")
                        + " (a type-returning body is evaluated at compile time; road-to-zig-std G4)");
            }
        }
        return null;
    }

    /// <summary>A <c>switch</c> STATEMENT in a type-returning body: the comptime subject selects one prong
    /// (S5a's <see cref="SelectComptimeProng"/>), its capture is bound for the arm, and only that arm is
    /// walked — a block (walked as statements; an empty one falls through), a <c>return</c>, or a nested
    /// <c>switch</c> expression whose own prongs return (<c>.pointer =&gt; |info| switch (info.size) {…}</c>).
    /// A prong that is any other bare expression is a loud cut: in statement position it would be a value
    /// discarded, which zig rejects too.</summary>
    private TypeBodyResult? WalkComptimeSwitchStmt(string fnName, Item subject, Item prongs,
        List<(string name, CType? prev, int? prevBits)> typeShadows)
    {
        if (SelectComptimeProng(subject, prongs, out var payload) is not { } prong)
        {
            throw new IrUnsupportedException(
                $"type-returning generic '{fnName}': a `switch` in a type body must be over a comptime subject "
                + "(`@typeInfo(T)`, a comptime tag)");
        }
        EnterComptimeProng(prong, payload);
        try
        {
            if (prong.Block is { } block) { return WalkTypeBody(fnName, BodyStatements(block), typeShadows); }
            if (prong.Return is { } ret)
            {
                var (t, bits) = LowerComptimeTypeExpr(fnName, ret);
                return new TypeBodyResult(false, null, t, bits);
            }
            if (prong.Expr is { } e)
            {
                var cur = e;
                while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
                switch (cur.Content)
                {
                    case Zig.SwitchExpr inner:         return WalkComptimeSwitchStmt(fnName, inner.Arg2, inner.Arg5, typeShadows);
                    case Zig.SwitchExprTrailing inner: return WalkComptimeSwitchStmt(fnName, inner.Arg2, inner.Arg5, typeShadows);
                    case Zig.BuiltinCall ce when Tok(ce.Arg0) == "@compileError":
                        CompileErrorBuiltin(Flatten(ce.Arg2));
                        return null;
                }
            }
            throw new IrUnsupportedException(
                $"type-returning generic '{fnName}': a `switch` statement prong in a type body must be a block, a "
                + "`return <type>`, or a nested `switch` — a bare value would be discarded");
        }
        finally
        {
            ExitComptimeProng();
        }
    }

    /// <summary>Whether a type body's <c>const</c> RHS denotes a TYPE (bound as an alias) rather than a
    /// comptime VALUE. The comptime control-flow shapes (<c>if</c> / captured <c>if</c> / <c>switch</c>) are
    /// types here, as they always were in a W4 body; otherwise <see cref="TryTypeAliasRhs"/> decides, the
    /// same guarded recognizer a top-level <c>const</c> uses — so <c>const bits = @typeInfo(T).int.bits;</c>
    /// is a value and <c>const E = T;</c> a type.</summary>
    private bool IsTypeBodyTypeRhs(Item rhs)
    {
        var cur = rhs;
        while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
        return cur.Content is Zig.IfExpr or Zig.IfExprCapture or Zig.SwitchExpr or Zig.SwitchExprTrailing
            || TryTypeAliasRhs(rhs, out _);
    }

    /// <summary>Bind a comptime VALUE <c>const</c> in a type body (<c>const bits: u16 = @typeInfo(T).int.bits;
    /// const log2_bits = 16 - @clz(bits - 1);</c>): lower the initializer into a throwaway hoist buffer, fold
    /// it, and declare the name as a comptime symbol (<see cref="_comptimeVars"/> — the same binding a
    /// <c>comptime n: u16</c> parameter seed gets), so a later <c>@Int(.unsigned, log2_bits)</c> or array
    /// extent reads the literal. A value that does not fold is a loud cut: a type body has no runtime.</summary>
    private void BindTypeBodyComptimeValue(string fnName, string name, Item? typeAst, Item rhs)
    {
        var declared = typeAst is { } ta ? LowerType(ta) : null;
        CExpr value;
        using (EnterThrowawayHoist())
        {
            value = LowerExpr(rhs);
        }
        if (_ir.ConstEval(value) is not { } v)
        {
            throw new IrUnsupportedException(
                $"type-returning generic '{fnName}': `const {name}` must be compile-time-known "
                + "(a type body is evaluated at compile time)");
        }
        var type = declared ?? value.Type ?? CType.Long;
        var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = type });
        _comptimeVars[sym] = (v, type);
    }

    /// <summary>A plain <c>if</c> in a type-returning body: only the taken arm is walked (the other may
    /// name a type that does not exist for this instantiation — that is usually why it is there).</summary>
    private TypeBodyResult? WalkComptimeIf(string fnName, Item cond, Item thenArm, Item? elseArm,
        List<(string name, CType? prev, int? prevBits)> typeShadows)
    {
        var taken = FoldTypeBodyCondition(fnName, cond) ? thenArm : elseArm;
        return taken is { } arm ? WalkTypeBody(fnName, BodyStatements(arm), typeShadows) : null;
    }

    /// <summary>A captured <c>if (opt) |x|</c> in a type-returning body, on a comptime optional (a
    /// <c>comptime alignment: ?mem.Alignment</c> seed): <c>null</c> takes the else arm, a known payload the
    /// then arm with <c>x</c> bound to it.</summary>
    private TypeBodyResult? WalkComptimeIfCapture(string fnName, Item cond, string capName, Item thenArm, Item? elseArm,
        List<(string name, CType? prev, int? prevBits)> typeShadows)
    {
        if (!TryComptimeOptionalCond(cond, out var copt))
        {
            throw new IrUnsupportedException(
                $"type-returning generic '{fnName}': a captured `if (x) |{capName}|` must test a comptime-known optional");
        }
        if (!copt.HasValue)
        {
            return elseArm is { } el ? WalkTypeBody(fnName, BodyStatements(el), typeShadows) : null;
        }
        _symbols.EnterScope();
        try
        {
            BindFoldedCapture(capName, copt.Value, copt.Inner);
            return WalkTypeBody(fnName, BodyStatements(thenArm), typeShadows);
        }
        finally
        {
            _symbols.ExitScope();
        }
    }

    /// <summary>Settle a type-returning body's condition at comptime: a tag / module-bool / type-equality
    /// question (<see cref="TryFoldComptimeCondition"/>), else an ordinary expression over the comptime
    /// VALUE seeds (<c>comptime n: u16</c> → <c>n &gt; 8</c>), lowered into a throwaway hoist buffer
    /// because nothing of it may reach an emitted body. Anything that does not settle is a loud cut — a
    /// type cannot depend on a runtime value.</summary>
    private bool FoldTypeBodyCondition(string fnName, Item cond)
    {
        if (TryFoldComptimeCondition(cond) is { } folded) { return folded; }
        using (EnterThrowawayHoist())
        {
            if (_ir.ConstEval(LowerExpr(cond)) is { } v) { return v != 0; }
        }
        throw new IrUnsupportedException(
            $"type-returning generic '{fnName}': an `if` condition must be compile-time-known "
            + "(a comptime parameter, a `builtin` query, or a type comparison)");
    }

    /// <summary>Lower an expression that must denote a TYPE, folding the comptime control flow a type
    /// expression can be built from — the shapes a type-returning body returns and its aliases bind:
    /// <list type="bullet">
    /// <item><c>if (c) A else B</c> — <paramref name="fnName"/>'s condition folds; only the taken arm lowers;</item>
    /// <item><c>if (opt) |x| A else B</c> — on a comptime optional (<c>Aligned</c>'s <c>Slice</c>);</item>
    /// <item><c>switch (@typeInfo(T)) { .pointer =&gt; |info| info.child, … }</c> — the S5a prong
    /// selection, with the capture bound for the arm (<c>std.meta.Child</c>);</item>
    /// <item><c>@compileError(…)</c> reached in a taken arm — raised, which is zig's rule;</item>
    /// <item>anything else — an ordinary type (<see cref="LowerType"/>), which already covers a
    /// delegating type-returning call, local or module-qualified, and <c>@Int(…)</c>.</item>
    /// </list>
    /// Returns the type together with its declared integer width (<see cref="_declaredIntBits"/>'s
    /// question), read off the arm that was actually TAKEN — so <c>if (n &gt; 8) u16 else u12</c> answers
    /// 12 for a small <c>n</c>, not the 16 dotcc widens <c>u12</c> to.</summary>
    private (CType Type, int? Bits) LowerComptimeTypeExpr(string fnName, Item expr)
    {
        var cur = expr;
        while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
        switch (cur.Content)
        {
            case Zig.IfExpr ie:
                return LowerComptimeTypeExpr(fnName, FoldTypeBodyCondition(fnName, ie.Arg2) ? ie.Arg4 : ie.Arg6);
            case Zig.IfExprCapture ic:
            {
                if (!TryComptimeOptionalCond(ic.Arg2, out var copt))
                {
                    throw new IrUnsupportedException(
                        $"type-returning generic '{fnName}': a captured `if (x) |{Tok(ic.Arg5)}|` type must test a "
                        + "comptime-known optional");
                }
                if (!copt.HasValue) { return LowerComptimeTypeExpr(fnName, ic.Arg9); }   // null → the else type
                _symbols.EnterScope();
                try
                {
                    BindFoldedCapture(Tok(ic.Arg5), copt.Value, copt.Inner);
                    return LowerComptimeTypeExpr(fnName, ic.Arg7);                        // payload → the then type
                }
                finally
                {
                    _symbols.ExitScope();
                }
            }
            case Zig.SwitchExpr se:
                return LowerComptimeTypeSwitch(fnName, se.Arg2, se.Arg5);
            case Zig.SwitchExprTrailing st:
                return LowerComptimeTypeSwitch(fnName, st.Arg2, st.Arg5);
            case Zig.BuiltinCall b when Tok(b.Arg0) == "@compileError":
                CompileErrorBuiltin(Flatten(b.Arg2));   // always throws — the arm was reached
                return (CType.Void, null);
            default:
            {
                var t = LowerType(expr);
                return (t, DeclaredBitsOfTypeArg(expr));   // after the lowering — see _typeCallBits
            }
        }
    }

    /// <summary>A <c>switch</c> whose prongs are TYPES: the comptime subject selects one prong (S5a's
    /// <see cref="SelectComptimeProng"/>), its capture is bound to the subject's payload for the arm, and
    /// only that arm lowers. A prong whose body is a block or a <c>return</c> is a loud cut — a value-
    /// position type switch is a bare expression in every std use.</summary>
    private (CType Type, int? Bits) LowerComptimeTypeSwitch(string fnName, Item subject, Item prongs)
    {
        if (SelectComptimeProng(subject, prongs, out var payload) is not { } prong)
        {
            throw new IrUnsupportedException(
                $"type-returning generic '{fnName}': a type `switch` must be over a comptime subject "
                + "(`@typeInfo(T)`, a comptime tag)");
        }
        if (prong.Expr is not { } armExpr)
        {
            throw new IrUnsupportedException(
                $"type-returning generic '{fnName}': a type `switch` prong must be a bare type expression "
                + "(a block or `return` prong is not supported yet)");
        }
        EnterComptimeProng(prong, payload);
        try
        {
            return LowerComptimeTypeExpr(fnName, armExpr);
        }
        finally
        {
            ExitComptimeProng();
        }
    }
}
