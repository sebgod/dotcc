#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Returns and control-flow fallbacks: <c>return</c> (with an error union, an error literal, a value
/// <c>if</c>), and <c>catch</c> / <c>orelse</c> whose fallback returns, breaks, continues, runs a block or a switch.
/// One concern of the <see cref="ZigLowering"/> binder.</summary>
internal sealed partial class ZigLowering
{
    private CStmt LowerReturn(Item valueItem)
    {
        // A runtime integer wider than the declared result is zig's "expected type" error (task #165).
        RejectIntegerNarrowing(valueItem,
            _currentFnRet is CType.ErrorUnion { Payload: var returnedPayload } ? returnedPayload : _currentFnRet,
            _currentFnSym is { } returningFn && _fnReturnBits.TryGetValue(returningFn, out var returnBits) ? returnBits : null);
        // `return {};` — the void value is what a bare `return;` returns: nothing to spell in C#
        // (`default(void)` is not an expression). In a `!void` function it is the success value.
        if (valueItem.Content is Zig.VoidValue)
        {
            return _currentFnRet is CType.ErrorUnion voidEu
                ? new Return(new ErrUnionOk(null) { Type = voidEu })
                : new Return(null);
        }
        // `return unmanaged.replaceRangeAssumeCapacity(…);` in a `void` function (std.array_list): zig returns
        // the void call's (void) value; C# forbids a value on a void return, so the call is a statement first.
        if (_currentFnRet?.Unqualified is CType.VoidType)
        {
            return new Seq(new List<CStmt> { new ExprStmt(LowerExpr(valueItem)), new Return(null) });
        }
        // The same in a `!void` function (`return self.insertAssumeCapacity(i, item);`): run the void call, then
        // return success.
        if (_currentFnRet is CType.ErrorUnion { Payload: CType.VoidType } voidUnion
            && valueItem.Content is Zig.CallArgs or Zig.CallNoArgs && IsVoidCall(valueItem))
        {
            return new Seq(new List<CStmt> { new ExprStmt(LowerExpr(valueItem)), new Return(new ErrUnionOk(null) { Type = voidUnion }) });
        }
        // `return blk: { … break :blk v; };` — a labeled value-block return (Milestone L, part 2).
        // Temp-fill against the function's return type, then `return` the result temp. (In an error-
        // union function the wrapping below would need to apply to the temp — deferred with a clear
        // error rather than silently returning an unwrapped value.)
        if (IsLabeledValue(valueItem))
        {
            if (_currentFnRet is CType.ErrorUnion)
            {
                throw new IrUnsupportedException(
                    "a labeled value-block `return blk: {…}` in an error-union (`!T`) function is not supported yet");
            }
            return LowerLabeledValue(valueItem, _currentFnRet,
                temp => new Return(new VarRef(temp) { Type = temp.Type }));
        }
        // `return switch (y) { … blk: {…} };` / `return if (c) blk:{…} else …;` — a value-position
        // if/switch with a statement-producing branch (Milestone Y, part 1): temp-fill against the
        // return type, then `return` the temp. An error-union `!T` function is deferred (like the
        // labeled-block return above — the ErrUnion wrapping would need to apply to the temp).
        if (IsValueControlFlowStmt(valueItem))
        {
            if (_currentFnRet is CType.ErrorUnion)
            {
                throw new IrUnsupportedException(
                    "a value-position `if`/`switch` with a block branch in an error-union (`!T`) function `return` is not supported yet");
            }
            return LowerValueControlFlowStmt(valueItem, _currentFnRet,
                temp => new Return(new VarRef(temp) { Type = temp.Type }));
        }
        if (_currentFnRet is CType.ErrorUnion eu)
        {
            // `return error.X;` or the set-qualified `return E.X;` (Milestone X, part 2) — both an
            // error return (the same flat code). Part 3: validate `E.X` membership, then reject an
            // error outside the function's DECLARED set (a good compiler rejects illegal programs).
            string? errName = null;
            if (IsErrorLit(valueItem, out var bareName)) { errName = bareName; }
            else if (TryErrorSetMember(valueItem, out var qSet, out var qName)) { ValidateSetMember(qSet, qName); errName = qName; }
            if (errName is not null)
            {
                CheckReturnedErrorInSet(errName);
                // With an `errdefer` in this function, the error must propagate via a thrown
                // ZigErrorReturn so it passes through the errdefer catch(es) on the stack (a C#
                // catch can't observe a direct return); the `!T` boundary catch converts it back
                // to an Err. Without an errdefer, keep the direct, exception-free Err return.
                var code = ErrorCode(errName);
                var codeLit = new LitInt(code.ToString(CultureInfo.InvariantCulture), code) { Type = CType.ErrorSet };
                if (_currentFnHasErrdefer) { return new ZigErrorThrow(codeLit); }
                return new Return(new ErrUnionErr(codeLit) { Type = eu });
            }
            // A result-located literal (`return .{ .named = arg_name };` in std.fmt.Parser.specifier, a `!Specifier`)
            // takes the payload type; anything else keeps its own (a call may return the error union itself).
            // So does a result-location cast builtin (`return @intCast(total % 251);` in a `!u8` main).
            var v = valueItem.Content is Zig.AnonStructInit or Zig.AnonStructInitEmpty or Zig.EnumLit
                    || valueItem.Content is Zig.BuiltinCall { Arg0: var castTok }
                       && Tok(castTok) is "@intCast" or "@truncate" or "@ptrCast" or "@bitCast" or "@floatCast"
                          or "@intFromFloat" or "@floatFromInt" or "@enumFromInt" or "@alignCast"
                    // A value `if` at a slice payload (std.mem.join's `return if (zero) try allocator.dupe(…) else
                    // &[0]u8{};`): each arm coerces to the slice.
                    || valueItem.Content is Zig.IfExpr && eu.Payload.Unqualified is CType.Slice
                ? LowerExprSink(valueItem, eu.Payload)
                : LowerExpr(valueItem);
            if (UnifyErrUnionArms(v, eu) is { } unified) { v = unified; }
            if (v.Type.Unqualified is CType.ErrorUnion) { return new Return(v); }
            // An array (`return &[0]u8{};`) at a slice payload is that slice.
            if (eu.Payload.Unqualified is CType.Slice okSlice && (v.Type.Unqualified is CType.Array || PointedArray(v) is ({ }, _)))
            {
                v = CoerceToSlice(v, okSlice);
            }
            // `return e;` where `e` is an error VALUE (a `catch |e|` / `else => |e|` capture) — an ERROR
            // return of that runtime code, not a success wrapping it as the payload. (Unless the payload
            // type IS an error set — `!anyerror`, where returning one as a value is a success; zig types
            // that the same way: an error value coerces to the error half only when it isn't the payload.)
            if (v.Type.Unqualified is CType.ErrorSetType && eu.Payload.Unqualified is not CType.ErrorSetType)
            {
                if (_currentFnHasErrdefer) { return new ZigErrorThrow(v); }
                return new Return(new ErrUnionErr(v) { Type = eu });
            }
            return new Return(new ErrUnionOk(v) { Type = eu });
        }
        // An array-by-value return (the Milestone K cut, made sound). A `[N]T`-returning function
        // emits a `T*` signature, but `return t;` of a stackalloc array local would hand back a
        // dangling pointer into the dead callee frame — yet Zig arrays are value types. Copy the N
        // elements into a heap-owned buffer (ArrayByValReturn) so the result outlives the call. The
        // node's type is the array type, so the return coercion is a no-op. (An array in an `!T`
        // error-union function takes the path above — a follow-up; rare in practice.)
        if (_currentFnRet is CType.Array retArr && retArr.Count is int retN)
        {
            var src = LowerExprSink(valueItem, retArr);
            // `return "0001…"[value * 2 ..][0..2].*;` (std.fmt.digits2): a slice deref'd to its array lowers as the
            // slice, whose elements start at its data pointer.
            if (src.Type?.Unqualified is CType.Slice srcSlice)
            {
                src = new Member(src, "Ptr", false) { Type = new CType.Pointer(srcSlice.Element) };
            }
            return new Return(new ArrayByValReturn(src, retArr.Element, retN) { Type = retArr });
        }
        // The return type is the sink, so `return .member;` / `return .{…};` resolve against
        // a struct/enum-returning function.
        return new Return(LowerExprSink(valueItem, _currentFnRet));
    }
    /// <summary>Lower <c>return;</c>. In a <c>!void</c> function it is a success error union
    /// with no payload (<c>ErrUnion&lt;Unit&gt;.Ok(default)</c>); otherwise a plain
    /// <c>return;</c>.</summary>
    private CStmt LowerReturnVoid() =>
        _currentFnRet is CType.ErrorUnion eu
            ? new Return(new ErrUnionOk(null) { Type = eu })
            : new Return(null);
    /// <summary>Lower a <c>catch</c> fallback's VALUE at a statement-context position (a
    /// <c>const</c>/<c>var</c> initializer), returning the pre-statements that must run first plus
    /// the value expression. Three shapes:
    /// <list type="bullet">
    /// <item>no capture + a simple (re-evaluable, side-effect-free) fallback → empty pre + the eager
    /// <see cref="ZigCatch"/> (<c>ErrUnion.Catch(a, b)</c>, unchanged from Milestone B2);</item>
    /// <item>no capture + a side-effecting fallback → hoist the union to a single-eval <c>__cE</c>
    /// temp and make the fallback LAZY via a ternary <c>__cE.IsErr ? b : __cE.Value</c> (so <c>b</c>
    /// runs only on error);</item>
    /// <item>a capture <c>catch |e| b</c> → hoist the union, bind <c>e</c> to the flat error code
    /// (<see cref="CType.ErrorSet"/>), then the same lazy ternary with <c>e</c> in scope for
    /// <c>b</c>.</item>
    /// </list>
    /// The left operand must be an error union; the lazy ternary keeps Zig's evaluate-fallback-only-
    /// on-error semantics where the eager helper can't.</summary>
    private (List<CStmt> Pre, CExpr Value) LowerCatchValue(Item unionItem, string? capName, Item fallbackItem)
    {
        var union = LowerExpr(unionItem);
        // A `comptime_int` has no runtime form, so a union carrying one (std.crypto.sha2's `pub const digest_length =
        // std.math.divCeil(comptime_int, digest_bits, 8) catch unreachable;`, task #160) is a compile-time value: evaluated
        // now, it takes the `comptime` path below, as `comptime f() catch unreachable` does.
        if (union is not ComptimeFold { Resolved: not null }
            && union.Type.Unqualified is CType.ErrorUnion { Payload.Unqualified: CType.Prim { IsComptimeInt: true } }
            && _ir.ResolveComptimeFold(union is ComptimeFold pending ? pending.Inner : union) is { } comptimeIntUnion)
        {
            union = new ComptimeFold(union is ComptimeFold inner ? inner.Inner : union) { Type = union.Type, Resolved = comptimeIntUnion };
        }
        // `comptime f() catch unreachable` (std.Random.int's `comptime std.math.divCeil(u16, bits, 8) catch unreachable`,
        // task #117): zig's `comptime` takes the whole expression, so it is one compile-time value. dotcc's `comptime` binds
        // tighter, but its fold already evaluated the call: a success is the payload, and the fallback never runs.
        if (union is ComptimeFold { Resolved: { } comptimeValue } && comptimeValue.Type?.Unqualified is not CType.ErrorUnion)
        {
            // At the payload's own type (`u16` from `divCeil(u16, …)`), not the literal's default `int`.
            return (new List<CStmt>(), comptimeValue is LitInt && union.Type?.Unqualified is CType.ErrorUnion { Payload: var foldPayload }
                ? new Cast(foldPayload, comptimeValue) { Type = foldPayload }
                : comptimeValue);
        }
        if (union.Type.Unqualified is not CType.ErrorUnion eu)
        {
            throw new IrUnsupportedException("zig `catch` requires an error-union left operand");
        }
        var payload = eu.Payload;
        var pre = new List<CStmt>();

        // The fallback is result-located at the payload type: `bufPrint(…) catch "ERR"` coerces the string literal to the
        // slice the payload is (it had been left a `byte*` beside a `Slice<byte>`, CS0029).
        if (capName is null)
        {
            var fb = LowerExprSink(fallbackItem, payload);
            if (IsSimpleReeval(fb)) { return (pre, new ZigCatch(union, fb) { Type = payload }); }
            var ce = HoistCatchUnion(union, pre);
            return (pre, new CondExpr(
                new Member(ce, "IsErr", false) { Type = CType.Bool },
                fb,
                new Member(ce, "Value", false) { Type = payload }) { Type = payload });
        }

        // Capture form `catch |e| b`: hoist, bind `e`, then the lazy ternary (with `e` visible).
        var ceCap = HoistCatchUnion(union, pre);
        if (capName != "_")
        {
            var errSym = _symbols.Declare(new Symbol { Name = capName, Kind = SymKind.Var, Type = CType.ErrorSet });
            pre.Add(new DeclStmt(new List<LocalDecl> { new(errSym, new Member(ceCap, "Code", false) { Type = CType.ErrorSet }) }));
        }
        var fbCap = LowerExprSink(fallbackItem, payload);
        return (pre, new CondExpr(
            new Member(ceCap, "IsErr", false) { Type = CType.Bool },
            fbCap,
            new Member(ceCap, "Value", false) { Type = payload }) { Type = payload });
    }
    /// <summary>Hoist a (possibly side-effecting) error-union operand to a single-eval <c>__cE</c>
    /// temp unless it is already a bare variable; append the decl to <paramref name="pre"/> and
    /// return a reference for re-reading it (the <c>.IsErr</c>/<c>.Code</c>/<c>.Value</c> sites).</summary>
    private CExpr HoistCatchUnion(CExpr union, List<CStmt> pre)
    {
        if (union is VarRef) { return union; }
        var tmp = _symbols.Declare(new Symbol { Name = "__cE", Kind = SymKind.Var, Type = union.Type });
        pre.Add(new DeclStmt(new List<LocalDecl> { new(tmp, union) }));
        return new VarRef(tmp) { Type = union.Type, IsLValue = true };
    }
    /// <summary>Recognize a control-flow <c>catch</c>/<c>orelse</c> fallback — one whose failure path
    /// runs a STATEMENT rather than yielding a value: <c>a catch return [v]</c> / <c>a orelse return
    /// [v]</c> (Milestone N, part 6), and the statement-shaped arms (road-to-zig-std): <c>break
    /// [:l [v]]</c>, <c>continue [:l]</c>, a <c>{ … }</c> block, a <c>switch</c>, and after a capture
    /// <c>catch |e| return [v]</c>. Yields the left operand, whether it is a <c>catch</c> (vs
    /// <c>orelse</c>), the <c>|e|</c> capture name (catch only; null when absent), and the ARM node that
    /// <see cref="LowerFallbackArm"/> dispatches on (for the legacy return forms, the node itself).</summary>
    private static bool IsControlFlowFallback(Item it, out Item lhs, out bool isCatch, out string? capture, out Item arm)
    {
        capture = null;
        arm = it;
        switch (it.Content)
        {
            case Zig.OrElseReturn r:     lhs = r.Arg0; isCatch = false; return true;
            case Zig.OrElseReturnVoid r: lhs = r.Arg0; isCatch = false; return true;
            case Zig.CatchReturn r:      lhs = r.Arg0; isCatch = true;  return true;
            case Zig.CatchReturnVoid r:  lhs = r.Arg0; isCatch = true;  return true;
            case Zig.OrElseArm o:        lhs = o.Arg0; isCatch = false; arm = o.Arg2; return true;
            case Zig.CatchArm c:         lhs = c.Arg0; isCatch = true;  arm = c.Arg2; return true;
            case Zig.CatchCaptureArm c:  lhs = c.Arg0; isCatch = true;  arm = c.Arg5; capture = Tok(c.Arg3); return true;
            default: lhs = it; isCatch = false; return false;
        }
    }
    /// <summary><c>catch |e| return if (c) a else b</c> (task #146): the condition first, then one of two returns, which is
    /// what the value <c>if</c> under a <c>return</c> means. A comptime-known condition keeps only its return.</summary>
    private CStmt LowerReturnIf(Item condItem, Item thenItem, Item elseItem)
    {
        if (TryFoldComptimeCondition(condItem) is { } taken)
        {
            return Hoisted(() => LowerReturn(taken ? thenItem : elseItem));
        }
        var cond = LowerExpr(condItem);
        return new If(cond, Hoisted(() => LowerReturn(thenItem)), Hoisted(() => LowerReturn(elseItem)));
    }
    /// <summary>Lower a control-flow fallback's ARM (see <see cref="IsControlFlowFallback"/>) to the
    /// statement that runs on the error / none path. Every form reuses the ordinary statement lowering
    /// of the same construct — a <c>return</c>, a (labeled) <c>break</c> / <c>continue</c>, a block —
    /// so a fallback arm means exactly what that statement means where it stands. The value-yielding
    /// <c>switch</c> arm is handled by the caller (it fills a result, it is not a jump).</summary>
    private CStmt LowerFallbackArm(Item arm) => arm.Content switch
    {
        Zig.OrElseReturn r   => LowerReturn(r.Arg3),
        Zig.CatchReturn r    => LowerReturn(r.Arg3),
        Zig.OrElseReturnVoid or Zig.CatchReturnVoid => LowerReturnVoid(),
        Zig.FbReturn r       => LowerReturn(r.Arg1),
        Zig.FbReturnSwitch rs => LowerReturn(rs.Arg1),
        Zig.FbReturnIf ri    => LowerReturnIf(ri.Arg3, ri.Arg5, ri.Arg7),
        Zig.FbBreak          => LowerUnlabeledBreak(),
        Zig.FbContinue       => new Continue(),
        Zig.FbBreakLabel b   => LowerLabeledLoopJump(Tok(b.Arg2), isContinue: false),
        Zig.FbBreakLabelValue b => Hoisted(() => LowerLabeledBreak(Tok(b.Arg2), b.Arg3)),
        Zig.FbContinueLabel c => LowerLabeledLoopJump(Tok(c.Arg2), isContinue: true),
        Zig.FbBlock b        => LowerStmt(b.Arg0),
        _ => throw new IrUnsupportedException("internal: fallback arm " + (arm.Content?.GetType().Name ?? "null")),
    };
    /// <summary>Whether a fallback arm can finish NORMALLY — i.e. fall off its end into the code after
    /// it. A jump never does; a block does unless its last statement is itself a jump (or
    /// <c>unreachable</c> / <c>@panic</c>). Where the fallback must produce the payload (a <c>const v = a
    /// orelse { … };</c>), an arm that can fall through would leave <c>v</c> without a value — zig rejects
    /// that as a type error (a <c>void</c> block where a <c>T</c> is expected), and so does dotcc.</summary>
    private static bool ArmCanFallThrough(Item arm) => arm.Content switch
    {
        Zig.FbBlock b => b.Arg0.Content is not Zig.Block blk || Flatten(blk.Arg1) is not { Count: > 0 } stmts
                         || !IsNoReturnStmt(stmts[^1]),
        _ => false,
    };
    /// <summary>A statement that never completes normally: a <c>return</c>, a <c>break</c> /
    /// <c>continue</c> (labeled or not), <c>unreachable;</c>, or a <c>@panic(…)</c> / <c>@trap()</c> call.
    /// Conservative — a nested <c>if</c> whose both arms jump still counts as falling through.</summary>
    private static bool IsNoReturnStmt(Item stmt) => stmt.Content switch
    {
        Zig.StmtReturn or Zig.StmtReturnVoid or Zig.StmtBreak or Zig.StmtContinue
          or Zig.StmtBreakValue or Zig.StmtBreakLabelValue or Zig.StmtBreakLabel or Zig.StmtContinueLabel => true,
        Zig.StmtExpr { Arg0.Content: Zig.Ident u } when Tok(u.Arg0) == "unreachable" => true,   // an identifier in this grammar
        Zig.StmtExpr { Arg0.Content: Zig.BuiltinCall bc } when Tok(bc.Arg0) is "@panic" or "@trap" => true,
        _ => false,
    };
    /// <summary>Lower a control-flow <c>catch</c>/<c>orelse</c> fallback (Milestone N, part 6): <c>a
    /// catch return [v]</c> / <c>a orelse return [v]</c>. The left operand — an error union (for
    /// <c>catch</c>) or an optional (for <c>orelse</c>) — is hoisted to a single-eval temp; on the
    /// error / none path the <c>return</c> runs as an EARLY-OUT (lowered via
    /// <see cref="LowerReturn"/>/<see cref="LowerReturnVoid"/>, so it wraps correctly in a <c>!T</c>
    /// function — incl. <c>return error.X</c>). On the success path the unwrapped payload is consumed
    /// by <paramref name="bind"/> (a decl initializer binds it; an expression-statement passes null
    /// and discards it). Emitted as <c>{ var __cf = a; if (Cond.B(&lt;none/error&gt;)) { return …; }
    /// [bind(payload)] }</c>. <paramref name="resultSink"/> is the declared type of what binds the result, when there is
    /// one: an OPTIONAL there types a value arm's result (see below).</summary>
    private CStmt LowerControlFlowFallback(Item lhsItem, bool isCatch, string? capture, Item arm, Func<CExpr, CStmt>? bind,
        CType? resultSink = null)
    {
        var lhs = LowerExpr(lhsItem);
        // An optional dotcc already folded to its compile-time VALUE (std.unicode's `std.simd.suggestVectorLength(u16) orelse
        // break :vectorized`, task #144): with no none path, the fallback never runs and the payload is that value.
        if (!isCatch && lhs.Type.Unqualified is not (CType.Optional or CType.Pointer) && ComptimeIntValue(lhs) is { } knownPayload)
        {
            var payloadLit = new LitInt(knownPayload.ToString(CultureInfo.InvariantCulture), knownPayload) { Type = lhs.Type };
            return bind is null ? new Seq(new List<CStmt>()) : bind(payloadLit);
        }
        // A compile-time-known OPTIONAL (a call returning `?comptime_int`, which zig evaluates at compile time: `const n =
        // pick(T) orelse break :blk;`, task #149): a value binds as the payload, and null takes a jump arm NOW. The jump ends
        // the block, whose rest zig never analyses (LowerStmtsWithDefers stops after a terminator), so a `@Vector(n, u8)`
        // past it is never lowered.
        var knownOptional = lhs switch
        {
            ComptimeFold { Resolved: { } resolved } when lhs.Type.Unqualified is CType.Optional => resolved,
            DefaultLit when lhs.Type.Unqualified is CType.Optional && ReturnsOptionalComptimeInt(lhsItem) => lhs,
            _ => null,
        };
        if (!isCatch && knownOptional is not null && lhs.Type.Unqualified is CType.Optional { Inner: var knownInner })
        {
            if (knownOptional is DefaultLit && arm.Content is not (Zig.FbSwitch or Zig.FbLabeled) && !ArmCanFallThrough(arm))
            {
                return LowerFallbackArm(arm);
            }
            if (knownOptional is not DefaultLit && ComptimeIntValue(knownOptional) is { } knownValue)
            {
                var payloadLit = new LitInt(knownValue.ToString(CultureInfo.InvariantCulture), knownValue) { Type = knownInner };
                return bind is null ? new Seq(new List<CStmt>()) : bind(payloadLit);
            }
        }
        var pre = new List<CStmt>();
        CExpr lhsRef;
        if (lhs is VarRef) { lhsRef = lhs; }
        else
        {
            var tmp = _symbols.Declare(new Symbol { Name = "__cf", Kind = SymKind.Var, Type = lhs.Type });
            pre.Add(new DeclStmt(new List<LocalDecl> { new(tmp, lhs) }));
            lhsRef = new VarRef(tmp) { Type = lhs.Type, IsLValue = true };
        }

        CExpr test;       // true on the path that must `return` (error for catch, none for orelse)
        CExpr payload;    // the unwrapped success value
        var ct = lhsRef.Type.Unqualified;
        if (isCatch)
        {
            if (ct is not CType.ErrorUnion eu)
            {
                throw new IrUnsupportedException("zig `catch` with a control-flow fallback requires an error-union left operand");
            }
            test = new Member(lhsRef, "IsErr", false) { Type = CType.Bool };
            payload = new Member(lhsRef, "Value", false) { Type = eu.Payload };
            // A `create`-style error-union-over-pointer (`Error!*T`, Milestone U) carries its payload
            // as a `nuint` (a pointer can't be an `ErrUnion<T>` generic arg), so `.Value` is a `nuint`;
            // cast it back to the `T*` the payload names. Mirrors the `try` lowering (PreTry above);
            // `create` is the only producer of a pointer-payload union, so the cast is exactly correct.
            if (eu.Payload.Unqualified is CType.Pointer)
            {
                payload = new Cast(eu.Payload, payload) { Type = eu.Payload };
            }
        }
        else if (ct is CType.Optional opt)
        {
            test = new Unary(UnOp.LogNot, new Member(lhsRef, "HasValue", false) { Type = CType.Bool }) { Type = CType.Int };
            payload = new Member(lhsRef, "Value", false) { Type = opt.Inner };
        }
        else if (ct is CType.Pointer)
        {
            // A niche optional pointer (`?*T` → bare `T*`): none is null, the unwrapped value is the
            // pointer itself.
            test = new Binary(BinOp.Eq, lhsRef, new NullPtr { Type = new CType.Pointer(CType.Void) }) { Type = CType.Int };
            payload = lhsRef;
        }
        else
        {
            throw new IrUnsupportedException("zig `orelse` with a control-flow fallback requires an optional left operand");
        }

        // A value-yielding `switch` arm fills a result declared in the ENCLOSING scope (the consumer
        // reads it after the `if`), so it is declared before the failure path's own scope opens.
        // A `switch` arm over a `!void` whose result nobody binds (`self.shrinkAndFreePrecise(…) catch |e|
        // switch (e) { error.OutOfMemory => { …; return; } };` in array_list) yields no value: it is a
        // statement switch on the failure path, so its prongs may be void blocks.
        var voidSwitch = arm.Content is Zig.FbSwitch && bind is null && payload.Type.Unqualified.Equals(CType.Void);
        // The value arm's result is at the RESULT type, which zig takes from the result location: `const lnum: ?usize =
        // parseUnsigned(…) catch |err| switch (err) { error.InvalidCharacter => null, … };` (std.SemanticVersion.order,
        // task #195) is a `?usize` whose prong may be `null`, not the `usize` payload. Only an optional sink over the payload
        // widens it; any other declared type keeps the payload's (the binding coerces it as before).
        if (resultSink?.Unqualified is CType.Optional { Inner: var sinkInner }
            && sinkInner.Unqualified.Equals(payload.Type.Unqualified)
            && arm.Content is Zig.FbSwitch or Zig.FbLabeled)
        {
            payload = new Cast(resultSink, payload) { Type = resultSink };
        }
        Symbol? switchResult = arm.Content is Zig.FbSwitch or Zig.FbLabeled && !voidSwitch
            ? _symbols.Declare(new Symbol { Name = "__cfv" + _anfTempCounter++, Kind = SymKind.Var, Type = payload.Type })
            : null;
        // The failure path, in its own scope: `catch |e|` binds the error code first (a `_` binds
        // nothing), then the arm runs.
        _symbols.EnterScope();
        try
        {
            var onFail = new List<CStmt>();
            if (capture is { } cap && cap != "_")
            {
                var errSym = _symbols.Declare(new Symbol { Name = cap, Kind = SymKind.Var, Type = CType.ErrorSet });
                onFail.Add(new DeclStmt(new List<LocalDecl> { new(errSym, new Member(lhsRef, "Code", false) { Type = CType.ErrorSet }) }));
            }
            if (arm.Content is Zig.FbSwitch fs && switchResult is { } result)
            {
                // A value-yielding `switch` arm (`a catch |err| switch (err) { error.X => 0, else => return err }`)
                // FILLS a result on the failure path — each prong a value or a jump — and the success path
                // fills it with the payload, so the consumer reads one temp either way.
                var resultRef = new VarRef(result) { Type = payload.Type, IsLValue = true };
                onFail.Add(LowerValueControlFlowStmt(fs.Arg0, payload.Type, temp =>
                    new ExprStmt(new Assign(null, resultRef, new VarRef(temp) { Type = temp.Type }) { Type = payload.Type })));
                pre.Add(new DeclStmt(new List<LocalDecl> { new(result, null) }));
                pre.Add(new If(test, new Block(onFail),
                    new Block(new List<CStmt> { new ExprStmt(new Assign(null, resultRef, payload) { Type = payload.Type }) })));
                // The consumer binds AFTER the failure scope closes (below) — a `const v = …` must be
                // visible to the statements that follow, not only inside the arm.
                payload = new VarRef(result) { Type = payload.Type };
            }
            else if (arm.Content is Zig.FbLabeled { Arg0.Content: Zig.LabeledBlock lb } && switchResult is { } blkResult)
            {
                // A labeled VALUE block arm (`x orelse init: { …; break :init v; }`, std.fmt.ArgState): it
                // fills the result on the failure path only, the payload fills it otherwise.
                var resultRef = new VarRef(blkResult) { Type = payload.Type, IsLValue = true };
                onFail.Add(LowerLabeledValueBlock(Tok(lb.Arg0), lb.Arg2, payload.Type, temp =>
                    new ExprStmt(new Assign(null, resultRef, new VarRef(temp) { Type = temp.Type }) { Type = payload.Type })));
                pre.Add(new DeclStmt(new List<LocalDecl> { new(blkResult, null) }));
                pre.Add(new If(test, new Block(onFail),
                    new Block(new List<CStmt> { new ExprStmt(new Assign(null, resultRef, payload) { Type = payload.Type }) })));
                payload = new VarRef(blkResult) { Type = payload.Type };
            }
            else if (voidSwitch && arm.Content is Zig.FbSwitch { Arg0.Content: var voidSw })
            {
                var (swSubject, swProngs) = voidSw switch
                {
                    Zig.SwitchExpr se => (se.Arg2, se.Arg5),
                    Zig.SwitchExprTrailing st => (st.Arg2, st.Arg5),
                    _ => throw new IrUnsupportedException("zig switch arm: " + (voidSw?.GetType().Name ?? "null")),
                };
                onFail.Add(LowerSwitchStmt(swSubject, swProngs));
                pre.Add(new If(test, new Block(onFail), null));
            }
            else
            {
                if (bind is not null && ArmCanFallThrough(arm))
                {
                    throw new IrUnsupportedException(
                        $"zig `{(isCatch ? "catch" : "orelse")} {{ … }}`: where the fallback must produce a value, the block must "
                        + "not fall through — end it with `return`, `break`, `continue` or `unreachable` (a `void` block is not a "
                        + "value of the payload type; zig rejects this too)");
                }
                onFail.Add(LowerFallbackArm(arm));
                pre.Add(new If(test, new Block(onFail), null));
            }
        }
        finally
        {
            _symbols.ExitScope();
        }
        if (bind is not null) { pre.Add(bind(payload)); }
        return pre.Count == 1 ? pre[0] : new Seq(pre);
    }
    /// <summary>A value <c>if (c) return v else w</c> (std.fmt.parse_float's FloatStream.first): the then arm leaves the
    /// function, so the statement hoists <c>if (c) return v;</c> ahead of itself and the expression is <c>w</c>. The
    /// condition is evaluated exactly once, before the rest of the statement, as zig does; a comptime-false one
    /// drops the return.</summary>
    private CExpr LowerIfReturnThen(Item condItem, Item returnedItem, Item elseItem, CType? sink)
    {
        if (TryFoldComptimeCondition(condItem) is false)
        {
            return sink is null ? LowerExpr(elseItem) : LowerExprSink(elseItem, sink);
        }
        var savedImpure = _hoistImpureSeen;
        var cond = LowerExpr(condItem);
        var early = Hoisted(() => LowerReturn(returnedItem));
        _hoistImpureSeen = savedImpure;
        RequireHoistable("zig value `if (c) return x else y`").Add(new If(cond, early, null));
        return sink is null ? LowerExpr(elseItem) : LowerExprSink(elseItem, sink);
    }
}
