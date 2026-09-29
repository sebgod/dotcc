#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Statements run at lowering time: <c>comptime var</c>, <c>comptime { … }</c> blocks and the statements
/// inside them (assignments, <c>while</c>, calls folded to a bool), and a <c>comptime if</c> statement. One concern of
/// the <see cref="ZigLowering"/> binder.</summary>
internal sealed partial class ZigLowering
{
    /// <summary>Lower a <c>comptime var</c> / <c>comptime const</c> declaration (Milestone T, part 3):
    /// fold the initializer to a compile-time integer and track it by Symbol identity, emitting NO
    /// runtime declaration — references substitute its current value (see the <c>Zig.Ident</c> case).
    /// The declared type is the explicit annotation or the initializer's type. Only the integer value
    /// subset is supported (the firewall — no comptime pointer/aggregate var).</summary>
    private CStmt LowerComptimeVarDecl(Item varDeclItem)
    {
        TrackComptimeVar(varDeclItem);
        return new Seq(new List<CStmt>());   // comptime-only — no runtime declaration
    }
    /// <summary>Fold a <c>var</c>/<c>const</c> declaration's initializer to a compile-time integer and
    /// track it by Symbol identity in <see cref="_comptimeVars"/> (so references substitute the value).
    /// Shared by <c>comptime var</c> statements and the bodies of a <c>comptime { … }</c> block, where
    /// every declaration is a compile-time value. Only the integer value subset (the firewall).</summary>
    private void TrackComptimeVar(Item varDeclItem)
    {
        string name;
        Item initItem;
        Item? typeItem = null;
        switch (varDeclItem.Content)
        {
            case Zig.ConstDecl d:      name = Tok(d.Arg1); initItem = d.Arg3; break;
            case Zig.VarDecl d:        name = Tok(d.Arg1); initItem = d.Arg3; break;
            case Zig.ConstDeclTyped d: name = Tok(d.Arg1); typeItem = d.Arg3; initItem = d.Arg5; break;
            case Zig.VarDeclTyped d:   name = Tok(d.Arg1); typeItem = d.Arg3; initItem = d.Arg5; break;
            default:
                throw new IrUnsupportedException(
                    "`comptime` here is only supported on a `var`/`const` value declaration");
        }
        // A comptime STRING var (`comptime var literal: []const u8 = "";`, std.Io.Writer.print).
        if (EvalComptimeValue(initItem) is LitStr initStr)
        {
            var stype = typeItem is { } st ? LowerType(st) : initStr.Type;
            var ssym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = stype });
            _comptimeStringVars[ssym] = initStr;
            return;
        }
        var declared = typeItem is { } ti ? LowerType(ti) : null;
        var initExpr = declared is { } dt ? LowerExprSink(initItem, dt) : LowerExpr(initItem);
        var ctype = declared ?? initExpr.Type;
        // A comptime STRUCT or ARRAY var (the comptime engine's E3, `comptime var arg_state: ArgState =
        // .{…}` in std.Io.Writer.print): the interpreter holds its value, and each reference is live.
        if (ctype.Unqualified is CType.Named or CType.Array)
        {
            if (_ir.EvalComptimeValue(initExpr) is not { } agg)
            {
                throw new IrUnsupportedException(
                    $"`comptime var {name}` initializer must be a compile-time-known value");
            }
            var aggSym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = ctype });
            _ir.ComptimeGlobals[aggSym] = agg;
            return;
        }
        if (_ir.ConstEval(initExpr) is not { } v)
        {
            throw new IrUnsupportedException(
                $"`comptime var {name}` initializer must be a compile-time-known integer constant");
        }
        var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = ctype });
        _comptimeVars[sym] = (v, ctype);
    }
    /// <summary>Lower a <c>comptime { … }</c> block statement (Milestone T, part 3): EXECUTE the block
    /// at lowering time, folding its comptime-value statements (var/const decls, assignments to comptime
    /// vars, comptime <c>while</c> loops) and mutating any enclosing <c>comptime var</c> in place. It
    /// emits NO runtime code — its only effect is on comptime values, which later references substitute.
    /// Block-local comptime vars are scoped so they don't leak past the block.</summary>
    private CStmt LowerComptimeBlock(Item blockItem)
    {
        // A comptime block that RETURNS the function's result (std.simd.iota's `comptime { var out: [len]T =
        // undefined; for (&out, 0..) |*e, i| …; return @as(@Vector(len, T), out); }`) is a computation over
        // comptime operands only, so running it at runtime gives zig's value; it lowers as a plain block.
        if (blockItem.Content is Zig.Block { Arg1: var stmtList } && Flatten(stmtList) is { Count: > 0 } stmts
            && stmts[^1].Content is Zig.StmtReturn ret)
        {
            // Only an `inline fn` may be CALLED AT RUNTIME with such a block (task #92): a plain one is recorded, and a
            // runtime call reaching it is rejected once the whole call graph is known.
            if (_currentFnSym is { } owner && !_zigInlineFns.Contains(owner))
            {
                _comptimeReturnFns.TryAdd(owner, $"zig: function called at runtime cannot return value at comptime ('{owner.Name}')");
            }
            return TryComptimeReturnBlock(stmts, ret) ?? LowerBlock(blockItem);
        }
        // A comptime block's calls run at compile time (task #92).
        using (EnterSymbolScope())
        using (EnterComptime()) { ExecuteComptimeStmt(blockItem); }
        return new Seq(new List<CStmt>());   // compile-time-only — nothing runs at runtime
    }
    /// <summary>The nesting depth of <see cref="TryComptimeReturnBlock"/>'s lowering: statements lowered only for the comptime
    /// interpreter to evaluate, where an address of comptime memory is not a dangling stack pointer.</summary>
    private int _loweringForComptimeEval;
    /// <summary>A <c>comptime { …; return &amp;final; }</c> block in a SLICE-returning function (std.enums.valuesFromFields):
    /// zig's slice points into comptime memory, but lowered as runtime code it would point into the frame's
    /// <c>stackalloc</c> and dangle once the function returns (a silent miscompile). So the block is lowered into a
    /// throwaway scope, its return value captured, and the whole run by the comptime interpreter; the evaluated slice
    /// becomes a pinned static. Null when the block does not evaluate at compile time (the caller lowers it plainly).</summary>
    private CStmt? TryComptimeReturnBlock(IReadOnlyList<Item> stmts, Zig.StmtReturn ret)
    {
        // A STRUCT result too (std.StaticStringMap.initComptime's `comptime { var self = Self{}; …; self.kvs = &.{ … };
        // return self; }`, task #100): as runtime code its pointers into the block's arrays dangle once the function returns
        // (a silent miscompile), so the struct is evaluated here and spliced with those arrays pinned.
        if (_currentFnRet?.Unqualified is not (CType.Slice or CType.Named) || _currentFnRet is not { } retSlice) { return null; }
        // Lowered and run ONE STATEMENT AT A TIME (task #100): each statement's locals become comptime values the next one's
        // lowering folds (an array sized by what the block computed so far, `[self.max_len + 1]u32`), and a `return`
        // anywhere (`if (kvs_list.len == 0) return self;`, the final one) is the block's result.
        var session = _ir.BeginComptimeSession();
        _symbols.EnterScope();
        _loweringForComptimeEval++;
        try
        {
            foreach (var stmt in stmts)
            {
                CStmt lowered;
                using (var hoist = EnterFreshHoist())
                {
                    var main = LowerStmt(stmt);
                    lowered = _hoist is { Count: > 0 } pre ? new Block([.. pre, main]) : main;
                }
                switch (_ir.RunComptimeSessionStmt(session, lowered))
                {
                    case null:
                        return null;
                    case (true, var value):
                        return value is IrModule.CtSlice or IrModule.CtStruct && _ir.SpliceComptimeValue(value) is { } spliced
                            ? new Return(spliced)
                            : null;
                }
            }
            return null;   // no statement returned: the block does not produce the function's result here
        }
        catch (IrUnsupportedException) { return null; }
        finally
        {
            _loweringForComptimeEval--;
            _symbols.ExitScope();
            _ir.EndComptimeSession(session);
        }
    }
    /// <summary>Execute one statement of a <c>comptime { … }</c> block at lowering time. Supports the
    /// compile-time value subset: nested blocks, <c>var</c>/<c>const</c> decls (tracked as comptime),
    /// assignment to a comptime var (folded + stored), and a <c>while</c> loop (interpreted, the body's
    /// assignments updating comptime vars). Any other statement — or an assignment to a non-comptime
    /// target — is a clear error (the firewall: no runtime effect, no pointer/aggregate mutation).</summary>
    private void ExecuteComptimeStmt(Item s)
    {
        switch (s.Content)
        {
            case Zig.Block b:
                foreach (var st in Flatten(b.Arg1)) { ExecuteComptimeStmt(st); }
                break;
            case Zig.BlockEmpty:
                break;
            case Zig.ComptimeVarDecl cv:
                TrackComptimeVar(cv.Arg1);
                break;
            case Zig.ConstDecl or Zig.VarDecl or Zig.ConstDeclTyped or Zig.VarDeclTyped:
                // Inside a comptime block every declaration is a compile-time value.
                TrackComptimeVar(s);
                break;
            case Zig.StmtAssign a:          // lhs = rhs
                ExecuteComptimeAssign(a.Arg0, a.Arg2);
                break;
            // A comptime `while (cond) : (cont) body` / `while (cond) body` — interpreted (the cont or
            // the body mutates the counter). `inline while` here would unroll-to-IR (wrong in a comptime
            // block); the plain `while` IS the comptime loop.
            case Zig.StmtWhileContAssign w:
                ExecuteComptimeWhile(w.Arg2, (w.Arg6, w.Arg8), w.Arg10);
                break;
            case Zig.StmtWhile w:
                ExecuteComptimeWhile(w.Arg2, null, w.Arg4);
                break;
            // `if (K == []const u8) @compileError(…);` (hash_map's getAutoHashFn): the condition folds and
            // only the taken branch runs, which is where zig raises a `@compileError`.
            case Zig.StmtIf i:
                if (FoldComptimeBlockCondition(i.Arg2)) { ExecuteComptimeStmt(i.Arg4); }
                break;
            case Zig.StmtIfElse i:
                ExecuteComptimeStmt(FoldComptimeBlockCondition(i.Arg2) ? i.Arg4 : i.Arg6);
                break;
            // `@compileError("…");` reached at comptime raises the author's message.
            case Zig.StmtExpr { Arg0.Content: Zig.BuiltinCall ce } when Tok(ce.Arg0) == "@compileError":
                LowerExpr(s.Content is Zig.StmtExpr se ? se.Arg0 : s);
                break;
            // `assert(c);` / `std.debug.assert(c);`: checked when `c` folds; otherwise an analysis-only
            // assertion with nothing to run.
            case Zig.StmtExpr { Arg0.Content: Zig.CallArgs { Arg0.Content: Zig.Ident or Zig.Field } ac }
                when CalleeLastName(ac.Arg0) == "assert" && Flatten(ac.Arg2) is { Count: 1 } assertArgs:
                if (TryFoldComptimeCondition(assertArgs[0]) is false)
                {
                    throw new IrUnsupportedException("zig: a comptime assertion failed (`assert` in a `comptime` block)");
                }
                break;
            // `if (n > 10) unreachable;` whose condition folds true (a comptime check, task #189): zig's compile error.
            case Zig.StmtExpr { Arg0: var reached } when IsUnreachableItem(reached):
                throw new CompileException("zig: reached unreachable code (in a `comptime` block)");
            default:
                throw new IrUnsupportedException(
                    $"comptime block: statement '{s.Content?.GetType().Name}' is not supported — only "
                    + "var/const decls, assignments to a comptime var, and `while` loops run at comptime");
        }
    }
    /// <summary>Fold a call to a generic of THIS module whose parameters are all <c>comptime T: type</c> and whose
    /// body is one <c>return &lt;question&gt;;</c> (auto_hash's <c>typeContainsSlice</c>): the arguments bind as
    /// type aliases (shadow-saved), the question folds through <see cref="TryFoldComptimeCondition"/>, and the
    /// caller's environment is restored. Null when the call is not of that shape or the question does not fold.</summary>
    private bool? TryFoldComptimeBoolCall(Item call)
    {
        if (call.Content is not Zig.CallArgs ca || _comptimeBoolCallDepth > 16) { return null; }
        (ZigLowering Owner, Symbol Sym)? target = ca.Arg0.Content switch
        {
            Zig.Ident id when (_symbols.Resolve(Tok(id.Arg0)) ?? (_lazy ? EnsureDeclLowered(Tok(id.Arg0)) : null)) is { } local
                => (this, local),
            // `std.meta.hasUniqueRepresentation(Key)`: the owner asks the question with the caller's types.
            Zig.Field f when !IsCuratedStdPath(ca.Arg0) && ResolveModulePath(f.Arg0)?.Lowering is { } mod
                             && mod.ResolveExportedDecl(Tok(f.Arg2)) is { } exported
                => (exported.Owner, exported.Sym),
            _ => null,
        };
        if (target is not { } t || !t.Owner._genericFns.TryGetValue(t.Sym, out var g)) { return null; }
        var args = Flatten(ca.Arg2);
        if (args.Count != g.Params.Count || g.Params.Any(p => p.Kind != ParamKind.ComptimeType)) { return null; }
        var resolved = args.Select(a => (LowerType(a).Unqualified, DeclaredBitsOfTypeArg(a))).ToList();
        return t.Owner.FoldBoolCallBody(g, resolved);
    }
    /// <summary>The owner-side half of <see cref="TryFoldComptimeBoolCall"/>: with the type parameters of
    /// <paramref name="g"/> bound to <paramref name="resolved"/>, fold its single <c>return</c>.</summary>
    private bool? FoldBoolCallBody(GenericFnInfo g, IReadOnlyList<(CType Type, int? Bits)> resolved)
    {
        if (_comptimeBoolCallDepth > 16 || BodyStatements(g.Body) is not { Count: 1 } stmts || stmts[0].Content is not Zig.StmtReturn ret)
        {
            return null;
        }
        var shadows = new List<(string Name, CType? Prev, int? PrevBits)>();
        _comptimeBoolCallDepth++;
        try
        {
            for (var i = 0; i < g.Params.Count; i++)
            {
                var pname = g.Params[i].Name;
                shadows.Add((pname, _typeAliases.TryGetValue(pname, out var pv) ? pv : null,
                             _declaredIntBits.TryGetValue(pname, out var pb) ? pb : null));
                _typeAliases[pname] = resolved[i].Type;
                SetDeclaredIntBits(pname, resolved[i].Bits);
            }
            return TryFoldComptimeCondition(ret.Arg1);
        }
        finally
        {
            _comptimeBoolCallDepth--;
            for (var i = shadows.Count - 1; i >= 0; i--)
            {
                var (pname, prev, prevBits) = shadows[i];
                if (prev is { } p) { _typeAliases[pname] = p; } else { _typeAliases.Remove(pname); }
                SetDeclaredIntBits(pname, prevBits);
            }
        }
    }
    /// <summary>Fold a comparison whose operands are compile-time constants, or null (a runtime operand, or
    /// one that does not lower here). Lowered into a throwaway hoist, so nothing it touches is emitted.</summary>
    private bool? TryConstEvalCondition(Item cond)
    {
        try
        {
            using (EnterThrowawayHoist())
            {
                return _ir.ConstEval(LowerExpr(cond)) is { } v ? v != 0 : null;
            }
        }
        catch (IrUnsupportedException)
        {
            return null;
        }
    }
    /// <summary>The nesting depth of <see cref="TryFoldComptimeBoolCall"/>, bounding a recursive question.</summary>
    private int _comptimeBoolCallDepth;
    /// <summary>The condition of an <c>if</c> inside a <c>comptime { … }</c> block, which must be compile-time
    /// known: a comptime question (<see cref="TryFoldComptimeCondition"/>) or a folded integer.</summary>
    private bool FoldComptimeBlockCondition(Item cond) =>
        TryFoldRequiredComptimeCondition(cond)
        ?? throw new IrUnsupportedException("zig: an `if` in a `comptime` block needs a compile-time-known condition");
    /// <summary>The last name of a callee (<c>assert</c> for <c>assert</c> and <c>std.debug.assert</c>), or null.</summary>
    private static string? CalleeLastName(Item callee) => callee.Content switch
    {
        Zig.Ident id => Tok(id.Arg0),
        Zig.Field f => Tok(f.Arg2),
        _ => null,
    };
    /// <summary>Apply a comptime assignment <c>lhs = rhs</c> inside a <c>comptime { … }</c> block: the
    /// target must resolve to a tracked comptime var (its bare name, NOT substituted), the value folds
    /// (with comptime vars substituted), and the result is stored back.</summary>
    private void ExecuteComptimeAssign(Item lhsItem, Item rhsItem)
    {
        if (lhsItem.Content is not Zig.Ident id
            || _symbols.Resolve(Tok(id.Arg0)) is not { } sym
            || !_comptimeVars.ContainsKey(sym))
        {
            throw new IrUnsupportedException(
                "comptime block: an assignment target must be a `comptime var` (no runtime store at comptime)");
        }
        if (_ir.ConstEval(LowerExpr(rhsItem)) is not { } v)
        {
            throw new IrUnsupportedException("comptime block: assignment value must be compile-time-known");
        }
        _comptimeVars[sym] = (v, _comptimeVars[sym].Type);
    }
    /// <summary>Interpret a comptime <c>while</c> at lowering time: fold the condition each round (with
    /// comptime vars substituted), execute the body's comptime statements, then apply the optional
    /// continue-expression — until the condition is false. Step-capped (a non-terminating comptime
    /// condition otherwise loops forever).</summary>
    private void ExecuteComptimeWhile(Item condItem, (Item Lhs, Item Rhs)? cont, Item bodyItem)
    {
        var steps = 0;
        while (true)
        {
            if (_ir.ConstEval(LowerExpr(condItem)) is not { } cond)
            {
                throw new IrUnsupportedException("comptime block: `while` condition must be compile-time-known");
            }
            if (cond == 0) { break; }
            if (++steps > InlineUnrollCap)
            {
                throw new IrUnsupportedException(
                    $"comptime block: `while` exceeded {InlineUnrollCap} iterations — a non-terminating comptime condition?");
            }
            ExecuteComptimeStmt(bodyItem);
            if (cont is { } c) { ExecuteComptimeAssign(c.Lhs, c.Rhs); }
        }
    }
    /// <summary>The literal a <c>comptime var</c> reference substitutes to — its current value at the
    /// declared type (a negative value as <c>-(magnitude)</c>, mirroring how the interpreter splices a
    /// signed constant).
    /// <para>A narrow UNSIGNED type (<c>u8</c>/<c>u16</c>) substitutes at <c>int</c> instead: the backend
    /// renders an unsigned-typed literal with a <c>u</c> suffix, and a <c>uint</c> literal does not
    /// implicitly assign to a <c>byte</c>/<c>ushort</c> sink (CS0266) — so <c>comptime n: u8</c> used as a
    /// field/return value emitted invalid C#. The value always fits <c>int</c>, so this is
    /// value-preserving. Same normalization (and reason) as <see cref="BindFoldedCapture"/>, applied here
    /// so EVERY comptime-var substitution path shares it: a W3a comptime-VALUE parameter seed, a
    /// <c>comptime var</c>, an <c>inline for</c> capture, and a reified generic's method-body
    /// seeds.</para></summary>
    private static CExpr ComptimeVarLit(long v, CType t)
    {
        if (t.Unqualified is CType.Prim { Integer: true, Signed: false, Bytes: <= 2 }) { t = CType.Int; }
        // A `u64` seed past `i64` is held as its bit pattern; spell the unsigned value (no long value, so nothing folds it
        // as a negative number).
        if (v < 0 && IsUnsigned64(t))
        {
            return new LitInt(unchecked((ulong)v).ToString(System.Globalization.CultureInfo.InvariantCulture), null) { Type = t };
        }
        if (v >= 0)
        {
            return new LitInt(v.ToString(System.Globalization.CultureInfo.InvariantCulture), v) { Type = t };
        }
        var mag = -(System.Int128)v;
        return new Unary(UnOp.Neg, new LitInt(mag.ToString(System.Globalization.CultureInfo.InvariantCulture), v == long.MinValue ? null : -v) { Type = t }) { Type = t };
    }
    /// <summary>True for a 64-bit unsigned integer type (<c>u64</c> / <c>usize</c>), whose comptime seeds may hold a value past
    /// <see cref="long.MaxValue"/> as its bit pattern.</summary>
    private static bool IsUnsigned64(CType t) => t.Unqualified is CType.Prim { Integer: true, Signed: false, Bytes: 8 };
    /// <summary>Lower a payload-capturing <c>if (cond) |x| then [else …]</c> (Milestone M). The
    /// branch test and the binding depend on the condition's lowered type:
    /// <list type="bullet">
    /// <item>a value optional <c>?T</c> (<see cref="CType.Optional"/>) → test <c>__cap.HasValue</c>,
    /// bind <c>x = __cap.Value</c> at the top of the then-branch;</item>
    /// <item>a niche optional pointer (lowered to a bare <c>T*</c>) → test the pointer for non-null
    /// (<c>__cap != null</c>), bind <c>x = __cap</c> (the unwrapped pointer is the
    /// same value);</item>
    /// <item>an error union <c>!T</c> (<see cref="CType.ErrorUnion"/>) → bind the success payload to
    /// <c>x</c> in the then-branch and (with <c>else |e|</c>) the error code to <c>e</c> in the
    /// else-branch — a value inspection of <c>.IsErr</c>, never a propagating <c>try</c>.</item>
    /// </list>
    /// The condition is hoisted to a single-eval temp unless it is already a bare variable (the test
    /// and the binding both read it). A capture name of <c>_</c> tests without binding. An
    /// <paramref name="errCapName"/> (an <c>else |e|</c>) is only valid on an error union.</summary>
    /// <summary>Lower a plain <c>if</c> statement. In a generic INSTANCE body (wall-plan W3a) a
    /// COMPTIME-KNOWN condition — one <see cref="IrModule.ConstEval"/> folds, because a comptime
    /// parameter substitutes a literal (e.g. <c>n &lt; 2</c>) — is a Zig comptime-if: only the TAKEN
    /// branch is lowered, so the dead branch's generic calls never instantiate. Combined with the
    /// block-level dead-code stop (<see cref="LowerStmtsWithDefers"/>), that's what lets a recursive
    /// comptime generic (<c>fib</c>) prune its base case and terminate. A RUNTIME condition (ConstEval
    /// returns null), or any <c>if</c> outside an instance body, lowers to the ordinary two-armed
    /// <see cref="If"/> — the condition is lowered exactly once either way.</summary>
    /// <summary>Lower a <c>comptime if (c) then [else e]</c> statement: the condition is evaluated at
    /// compile time, so it MUST fold (a tag question, or anything <see cref="IrModule.ConstEval"/>
    /// settles, a comptime var or capture included), and only the taken arm is lowered. A condition that
    /// does not fold is an error, as in zig, rather than a quiet runtime <c>if</c>.</summary>
    private CStmt LowerComptimeIfStmt(Item condItem, Item thenItem, Item? elseItem)
    {
        var taken = TryFoldComptimeCondition(condItem)
            ?? (_ir.ConstEval(LowerExpr(condItem)) is { } cv
                ? cv != 0
                : throw new IrUnsupportedException(
                    "zig `comptime if`: the condition is not known at compile time"));
        if (taken) { return LowerComptimeArm(thenItem); }
        return elseItem is { } other ? LowerComptimeArm(other) : new Seq(new List<CStmt>());
    }
    /// <summary>Lower the taken arm of a <c>comptime if</c>. Everything under <c>comptime</c> runs at
    /// compile time, so a block or an assignment is EXECUTED by the comptime evaluator
    /// (<see cref="LowerComptimeBlock"/>: <c>w = 20;</c> updates the <c>comptime var w</c>, and emits
    /// nothing) rather than lowered as runtime code, which would store into a substituted literal. Any
    /// other arm is compile-time control flow over the enclosing unrolled code (<c>break</c> out of an
    /// <c>inline for</c>, a <c>return</c>) and lowers as the statement it is.</summary>
    private CStmt LowerComptimeArm(Item arm)
        => arm.Content is Zig.Block or Zig.BlockEmpty or Zig.StmtAssign
            ? LowerComptimeBlock(arm)
            : LowerStmt(arm);
}
