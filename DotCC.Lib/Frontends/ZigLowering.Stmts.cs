#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Statements: <c>LowerStmt</c> and every statement-position lowering (decls,
/// assignment, control flow, defer/errdefer, switch, capture forms) plus the ANF
/// statement-hoist machinery. One concern of the <see cref="ZigLowering"/> binder;
/// class doc + shared state live in the main file.</summary>
internal sealed partial class ZigLowering
{
    // ---- statements ------------------------------------------------------

    private Block LowerBlock(Item block)
    {
        var items = new List<Item>();
        switch (block.Content)
        {
            case Zig.BlockEmpty: break;
            case Zig.Block b: items.AddRange(Flatten(b.Arg1)); break;
            default: throw new IrUnsupportedException("zig block: " + (block.Content?.GetType().Name ?? "null"));
        }
        return new Block(LowerStmtsWithDefers(items, 0));
    }

    /// <summary>Lower a block's statement items, restructuring <c>defer</c>/<c>errdefer</c> into
    /// nested <see cref="DeferGuard"/>s (Milestone H). Each guard wraps the statements that FOLLOW
    /// it within the block (built by recursion), so nesting them in lexical declaration order yields
    /// Zig's LIFO cleanup — the last-declared defer/errdefer is innermost, hence runs first. A
    /// <c>defer</c> guards every exit (a try/finally); an <c>errdefer</c> only the error exit (a
    /// try/catch). The cleanup expression is lowered AT the defer's position (before the rest), so it
    /// resolves against the variables in scope there — and runs reading their values at scope exit
    /// (its render site is the finally/catch), matching Zig's defer semantics.</summary>
    private List<CStmt> LowerStmtsWithDefers(List<Item> items, int start)
    {
        var stmts = new List<CStmt>();
        for (int i = start; i < items.Count; i++)
        {
            var it = items[i];
            Item? cleanupBody;
            bool onErrorOnly;
            switch (it.Content)
            {
                case Zig.StmtDefer d:    cleanupBody = d.Arg1; onErrorOnly = false; break;
                // `errdefer comptime unreachable;` (std.hash_map's putAssumeCapacityNoClobber): zig's compile-time
                // assertion that no error return follows. Valid zig never runs its body, so it lowers to nothing.
                case Zig.StmtErrdefer { Arg1: var assertItem } when IsComptimeUnreachableStmt(assertItem):
                    continue;
                case Zig.StmtErrdefer d: cleanupBody = d.Arg1; onErrorOnly = true;  break;
                default:
                    var lowered = LowerStmt(it);
                    stmts.Add(lowered);
                    // A statement that comptime-folded to an unconditional terminator (a taken
                    // `if (n < 2) return n;` in a generic instance, wall-plan W3a; debug.zig's
                    // `if (!runtime_safety) return;` under dotcc's ReleaseFast) makes the REST of the
                    // block comptime-DEAD, which zig does not analyse: stop, so a pruned branch's generic
                    // calls never instantiate and a name only the other mode declares (`.locked`) is never
                    // resolved. Sound everywhere: zig rejects unreachable code after a plain terminator,
                    // so only a folded one can have statements after it.
                    if (Terminates(lowered)) { return stmts; }
                    continue;
            }
            // An `errdefer` makes the function's later `return error.X` propagate via a thrown
            // ZigErrorReturn (so it reaches this catch) — flagged BEFORE lowering the rest, so every
            // statement the guard wraps sees it (a guarded error-return always follows its errdefer
            // lexically, hence is lowered after this point).
            if (onErrorOnly) { _currentFnHasErrdefer = true; }
            var cleanup = LowerStmt(cleanupBody);
            var rest = new Block(LowerStmtsWithDefers(items, i + 1));
            stmts.Add(new DeferGuard(rest, cleanup, onErrorOnly));
            return stmts;   // the remaining statements now live inside the guard
        }
        return stmts;
    }

    private CStmt LowerStmt(Item stmt)
    {
        if (IsRuntimeLoopStmt(stmt.Content) && !ReferenceEquals(_loopBeingWrapped, stmt))
        {
            return LowerLoopWithBreakTarget(stmt);
        }
        switch (stmt.Content)
        {
            // A `const` may be a comptime allocator/namespace binding (`const std = @import("std");`,
            // `const a = std.heap.page_allocator;`) — recorded with NO runtime decl (Milestone F).
            case Zig.ConstDecl d:       return DeclOrComptime(d.Arg1, null, d.Arg3);
            case Zig.ConstDeclTyped d:  return DeclOrComptime(d.Arg1, d.Arg3, d.Arg5);
            case Zig.VarDecl d:         return DeclOf(d.Arg1, null, d.Arg3);
            case Zig.VarDeclTyped d:    return DeclOf(d.Arg1, d.Arg3, d.Arg5);
            // The shared VarDecl nonterminal lets a statement-position `threadlocal var` parse;
            // real zig allows `threadlocal` only at container level, so reject it like zig does.
            case Zig.VarDeclThreadLocal:
                throw new IrUnsupportedException(
                    "'threadlocal' is only allowed on a container-level `var` (a function-local threadlocal is rejected by real zig too)");
            // An in-function container decl (wall-plan W2): `const P = struct { … };` inside a body.
            // Registered on the fly into the module type section (top-level containers pre-register in
            // pass 0; a local one is first seen here mid-pass-2) and emits NO runtime statement. A local
            // enum / union (task #111) registers the same way, fields only.
            case Zig.StructDecl s:       return LowerLocalStruct(Tok(s.Arg1), s.Arg5, AggregateLayout.Default);
            case Zig.StructDeclEmpty s:  return LowerLocalStruct(Tok(s.Arg1), null,   AggregateLayout.Default);
            case Zig.ExternStructDecl s: return LowerLocalStruct(Tok(s.Arg1), s.Arg6, AggregateLayout.Sequential);
            case Zig.PackedStructDecl s: return LowerLocalStruct(Tok(s.Arg1), s.Arg6, AggregateLayout.Packed);
            case Zig.PackedStructDeclBacked s: return LowerLocalStruct(Tok(s.Arg1), s.Arg9, AggregateLayout.Packed);
            case Zig.EnumDecl d:          return LowerLocalEnumOrUnion(Tok(d.Arg1), d);
            case Zig.EnumDeclTyped d:     return LowerLocalEnumOrUnion(Tok(d.Arg1), d);
            case Zig.UnionDeclEnum d:     return LowerLocalEnumOrUnion(Tok(d.Arg1), d);
            case Zig.UnionDeclTagged d:   return LowerLocalEnumOrUnion(Tok(d.Arg1), d);
            case Zig.UnionDeclUntagged d: return LowerLocalEnumOrUnion(Tok(d.Arg1), d);
            // `const/var x: T align(N)/linksection(".s") = e;` (Milestone R, part 5) — the modifiers
            // are a no-op on the managed target, so lower exactly like the unmodified typed decl
            // (the DeclMods arg is ignored). RhsExpr is one slot right of the Type (DeclMods between).
            case Zig.ConstDeclTypedMods d: return DeclOrComptime(d.Arg1, d.Arg3, d.Arg6);
            case Zig.VarDeclTypedMods d:   return DeclOf(d.Arg1, d.Arg3, d.Arg6);
            // `const a, const b = e;` (Milestone G) — destructure a tuple value: single-eval the
            // RHS, then bind each name to its positional element. See LowerDestructure.
            case Zig.StmtDestructure sd: return LowerDestructure(sd);
            // `return E;` — E may contain a hoistable catch/orelse in a sub-expression (ANF), so lower
            // under a hoist buffer (the hoisted temps run before the `return`).
            case Zig.StmtReturn r:      return Hoisted(() => LowerReturn(r.Arg1));
            case Zig.StmtReturnVoid:    return LowerReturnVoid();
            // `a catch return [x];` / `a orelse return [x];` as a STATEMENT (Milestone N, part 6) —
            // a control-flow early-out; the unwrapped value is discarded (common for a `!void` `a`).
            case Zig.StmtExpr e when IsControlFlowFallback(e.Arg0, out var cfL, out var cfC, out var cfCap, out var cfArm):
                return LowerControlFlowFallback(cfL, cfC, cfCap, cfArm, null);
            // `@setEvalBranchQuota(n);` — a COMPTIME budget setter (road-to-zig-std S7). It is a
            // statement because zig types it `void`; it raises the interpreter's step budget and emits
            // nothing, so no `_ = …;` discard is left behind.
            case Zig.StmtExpr { Arg0.Content: Zig.BuiltinCall q } when Tok(q.Arg0) == "@setEvalBranchQuota":
                SetEvalBranchQuota(Flatten(q.Arg2));
                return new Seq(new List<CStmt>());
            // `@branchHint(.cold);` (hash_map's grow path, std's error paths): a layout hint to zig's optimizer
            // that must be a block's first statement; dotcc has nothing to emit for it.
            case Zig.StmtExpr { Arg0.Content: Zig.BuiltinCall branchHint } when Tok(branchHint.Arg0) == "@branchHint":
                return new Seq(new List<CStmt>());
            // `@setRuntimeSafety(false);` (std.math.divCeil) / `@setFloatMode(.optimized);`: zig's per-scope safety and
            // float-mode switches. dotcc's C# is unchecked arithmetic with IEEE floats either way, so nothing to emit.
            case Zig.StmtExpr { Arg0.Content: Zig.BuiltinCall scopeMode } when Tok(scopeMode.Arg0) is "@setRuntimeSafety" or "@setFloatMode":
                return new Seq(new List<CStmt>());
            // `@disableInstrumentation();` / `@disableIntrinsics();` (std's panic and memcpy paths): hints to
            // zig's own codegen, with nothing for dotcc to emit.
            case Zig.StmtExpr { Arg0.Content: Zig.BuiltinCallNoArgs hint }
                when Tok(hint.Arg0) is "@disableInstrumentation" or "@disableIntrinsics":
                return new Seq(new List<CStmt>());
            // `comptime assert(c);` (std.math.cast's `comptime assert(@typeInfo(T) == .int);`): the assertion is
            // checked NOW. False is zig's compile error; true emits nothing. An `assert` returns void, which
            // the deferred comptime fold cannot splice, so it is never deferred. A condition that does not
            // fold here is taken on trust (a leniency: zig would evaluate it).
            case Zig.StmtExpr { Arg0.Content: Zig.PreComptime { Arg1.Content: Zig.CallArgs ac } }
                when IsAssertCallee(ac.Arg0) && Flatten(ac.Arg2) is { Count: 1 } assertArgs:
            {
                bool? holds = TryFoldComptimeCondition(assertArgs[0]);
                if (holds is null)
                {
                    CExpr cond;
                    using (EnterThrowawayHoist()) { cond = LowerExpr(assertArgs[0]); }
                    holds = _ir.ConstEval(cond) is { } cv ? cv != 0 : null;
                }
                if (holds == false) { throw new IrUnsupportedException("zig: a `comptime assert(…)` failed (a compile error in zig)"); }
                return new Seq(new List<CStmt>());
            }
            case Zig.StmtExpr e:        return Hoisted(() => new ExprStmt(LowerExpr(e.Arg0)));

            // `x = value;`  → an assignment used as a statement. `_ = value;` is Zig's
            // explicit DISCARD (it forbids ignoring a non-void result) — lower it to a
            // bare expression statement, evaluated for its side effects.
            // A `catch`/`orelse` in the RHS (or a discarded `_ = f(a catch b())`) may hoist (ANF), so
            // lower the assignment under a hoist buffer.
            case Zig.StmtAssign a:
                return LowerAssignStmt(a.Arg0, a.Arg2);

            // `x op= y` (compound assignment) → the shared Assign node with a non-null CompoundOp.
            // Each operator maps to the SAME BinOp the matching Zig binary op uses (Add/Sub/…), so
            // `+=` stays consistent with how Zig's `+` lowers — NOT C's promotion rules. The C#
            // backend renders a native `target op= rhs`, evaluating the lvalue exactly once (correct
            // binding for `a[i()] += 1` / `p.* += 1`). Zig has no `++`/`--`; `x += 1` is the idiom.
            case Zig.StmtAddAssign a:    return CompoundAssign(a.Arg0, BinOp.Add, a.Arg2);
            case Zig.StmtSubAssign a:    return CompoundAssign(a.Arg0, BinOp.Sub, a.Arg2);
            case Zig.StmtMulAssign a:    return CompoundAssign(a.Arg0, BinOp.Mul, a.Arg2);
            case Zig.StmtDivAssign a:    return CompoundAssign(a.Arg0, BinOp.Div, a.Arg2);
            case Zig.StmtModAssign a:    return CompoundAssign(a.Arg0, BinOp.Mod, a.Arg2);
            case Zig.StmtShlAssign a:    return CompoundAssign(a.Arg0, BinOp.Shl, a.Arg2);
            case Zig.StmtShrAssign a:    return CompoundAssign(a.Arg0, BinOp.Shr, a.Arg2);
            case Zig.StmtBitAndAssign a: return CompoundAssign(a.Arg0, BinOp.BitAnd, a.Arg2);
            case Zig.StmtBitOrAssign a:  return CompoundAssign(a.Arg0, BinOp.BitOr, a.Arg2);
            case Zig.StmtBitXorAssign a: return CompoundAssign(a.Arg0, BinOp.BitXor, a.Arg2);

            // `x op%= y` (wrapping compound assignment, Milestone P) → the SAME CompoundAssign node as
            // the plain form. A native C# `target op= rhs` already truncates the result back to the LHS
            // width in the project's unchecked context — exactly two's-complement wrap — so `+%=` and
            // `+=` lower identically (dotcc doesn't model Zig's plain-`+` safe-mode overflow trap).
            case Zig.StmtAddWrapAssign a: return CompoundAssign(a.Arg0, BinOp.Add, a.Arg2);
            case Zig.StmtSubWrapAssign a: return CompoundAssign(a.Arg0, BinOp.Sub, a.Arg2);
            case Zig.StmtMulWrapAssign a: return CompoundAssign(a.Arg0, BinOp.Mul, a.Arg2);

            // `x op|= y` (saturating compound assignment, Milestone P) → `x = ZigMath.Sat…(x, y)`.
            // No native C# saturating compound op exists, so it desugars to a plain assignment of the
            // clamping call (single-eval-guarded on the lvalue — see SatCompoundAssign).
            case Zig.StmtAddSatAssign a: return SatCompoundAssign(a.Arg0, "SatAdd", a.Arg2);
            case Zig.StmtSubSatAssign a: return SatCompoundAssign(a.Arg0, "SatSub", a.Arg2);
            case Zig.StmtMulSatAssign a: return SatCompoundAssign(a.Arg0, "SatMul", a.Arg2);

            // if (cond) then [else else]  — `then`/`else`/`body` are themselves Stmts
            // (a single statement or a brace Block), which LowerStmt handles uniformly.
            case Zig.StmtIf f:          return LowerIfStmt(f.Arg2, f.Arg4, null);
            case Zig.StmtIfElse f:      return LowerIfStmt(f.Arg2, f.Arg4, f.Arg6);
            case Zig.StmtComptimeIf f:     return LowerComptimeIfStmt(f.Arg3, f.Arg5, null);
            case Zig.StmtComptimeIfElse f: return LowerComptimeIfStmt(f.Arg3, f.Arg5, f.Arg7);
            case Zig.StmtComptimeIfSemi f:     return LowerComptimeIfStmt(f.Arg3, f.Arg5, null);
            case Zig.StmtComptimeIfElseSemi f: return LowerComptimeIfStmt(f.Arg3, f.Arg5, f.Arg7);

            // `if (opt) |x| then [else else]` — payload-capturing `if` (Milestone M). Binds the
            // optional's payload (value `?T` or niche pointer) — or, with `else |e|`, an
            // error-union's success/error (part 3) — in the matching branch. See LowerIfCapture.
            case Zig.StmtIfCapture f:        return LowerIfCapture(f.Arg2, Tok(f.Arg5), f.Arg7, null, null);
            case Zig.StmtIfCaptureElse f:    return LowerIfCapture(f.Arg2, Tok(f.Arg5), f.Arg7, f.Arg9, null);
            case Zig.StmtIfCaptureRef f:     return LowerIfCapture(f.Arg2, Tok(f.Arg6), f.Arg8, null, null, byRef: true);
            case Zig.StmtIfCaptureRefElse f: return LowerIfCapture(f.Arg2, Tok(f.Arg6), f.Arg8, f.Arg10, null, byRef: true);
            case Zig.StmtIfCaptureErrElse f: return LowerIfCapture(f.Arg2, Tok(f.Arg5), f.Arg7, f.Arg12, Tok(f.Arg10));
            // `if (c) return x else …;` — a `return Expr` then-arm (ReturnArm), otherwise the same `if`.
            case Zig.StmtIfReturnElse f:           return LowerIfStmt(f.Arg2, f.Arg4, f.Arg6);
            case Zig.StmtIfCaptureReturnElse f:    return LowerIfCapture(f.Arg2, Tok(f.Arg5), f.Arg7, f.Arg9, null);
            case Zig.StmtIfCaptureReturnErrElse f: return LowerIfCapture(f.Arg2, Tok(f.Arg5), f.Arg7, f.Arg12, Tok(f.Arg10));
            case Zig.ReturnArm r:                  return Hoisted(() => LowerReturn(r.Arg1));
            case Zig.StmtIfAssignElse f:           return LowerIfStmt(f.Arg2, f.Arg4, f.Arg6);
            case Zig.AssignArm a:                  return LowerAssignArm(a);
            case Zig.StmtWhile w:       return new While(LowerExpr(w.Arg2), LowerStmt(w.Arg4));

            // `while (cond) : (cont) body` → the C IR `For` (no init): the cont runs after each
            // iteration AND on `continue`, exactly matching C's for-update — so `continue`
            // inside the loop runs the cont, faithful to Zig. The assignment cont (`i += 1`,
            // `i = i + 1`, …) builds an Assign CExpr post over the AssignOp operator (mirroring the
            // Stmt assignment family via ContAssignPost); the bare-expr cont a plain one.
            case Zig.StmtWhileCont w:
                return new For(null, LowerExpr(w.Arg2), LowerExpr(w.Arg6), LowerStmt(w.Arg8));
            case Zig.StmtWhileContAssign w:
                return new For(null, LowerExpr(w.Arg2), ContAssignPost(w.Arg6, w.Arg7, w.Arg8), LowerStmt(w.Arg10));
            case Zig.StmtWhileContBlock w:
                return new For(null, LowerExpr(w.Arg2), ContBlockPost(w.Arg6), LowerStmt(w.Arg8));

            // `while (opt) |x| body` — optional payload capture-while (Milestone M, part 2). See
            // LowerWhileCapture (desugars to `while (true) { … if (has) { bind; body } else break; }`).
            case Zig.StmtWhileCapture w: return LowerWhileCapture(w.Arg2, Tok(w.Arg5), w.Arg7);
            // `while (opt) |x| body else elsebody` — the else runs on natural exit (payload null / a
            // user `break` skips it, matching Zig). `while (eu) |x| body else |e| elsebody` — the error
            // branch binds `e` and runs elsebody, then exits.
            case Zig.StmtWhileCaptureElse w:
                return LowerWhileCapture(w.Arg2, Tok(w.Arg5), w.Arg7, (w.Arg9, null));
            case Zig.StmtWhileCaptureErrElse w:
                return LowerWhileCapture(w.Arg2, Tok(w.Arg5), w.Arg7, (w.Arg12, Tok(w.Arg10)));
            // `while (opt) |x| : (cont) body` — capture-while with a continue-expression → the C `For`
            // IR (post = cont), so `continue` runs the cont. The assign form builds an `Assign` post
            // over the AssignOp operator (via ContAssignPost, like stmtWhileContAssign); the bare-expr
            // form a plain one.
            case Zig.StmtWhileCaptureCont w:
                return LowerWhileCapture(w.Arg2, Tok(w.Arg5), w.Arg11, null, () => LowerExpr(w.Arg9));
            case Zig.StmtWhileCaptureContAssign w:
                return LowerWhileCapture(w.Arg2, Tok(w.Arg5), w.Arg13, null,
                    () => ContAssignPost(w.Arg9, w.Arg10, w.Arg11));

            // `break;` / `continue;` — reuse the C IR loop-control nodes (the C# backend
            // renders them verbatim; valid inside the while/for forms above).
            // `return struct { pub fn inner … }.inner;` (the closure idiom): return the method as a function
            // value. An instance reified it when it was created; a plain function reifies it here.
            case Zig.ReturnStructMember rsm:
            {
                var method = ReifyClosureStruct(_currentFnName, rsm.Arg3, Tok(rsm.Arg6),
                    System.Array.Empty<TypeSeed>(), System.Array.Empty<(string, long, CType)>(),
                    System.Array.Empty<(string, bool, long, CType)>());
                return new Return(new VarRef(method) { Type = method.Type });
            }
            case Zig.StmtBreak:    return LowerUnlabeledBreak();
            case Zig.StmtContinue: return new Continue();

            // `break v;` — an unlabeled value break (Milestone Y, part 2): yield `v` from the innermost
            // value-position loop (`while/for … else`). Assigns its result temp and jumps to its end
            // label (skipping the loop's `else`).
            // The value's hoisted pre-statements (a struct literal's array copy-in, task #78) belong right before the
            // break, after the block's own locals; the enclosing statement's hoist would run them before the block.
            case Zig.StmtBreakValue b: return Hoisted(() => LowerBreakValue(b.Arg1));

            // `break :blk v;` — yield a value from the enclosing labeled value-block (Milestone L,
            // part 2). Assigns the block's result temp and jumps to its end label (LowerLabeledBreak).
            case Zig.StmtBreakLabelValue b: return Hoisted(() => LowerLabeledBreak(Tok(b.Arg2), b.Arg3));

            // `lbl: while/for (…) { … }` — a labeled loop (Milestone L, part 3); `break :lbl;` /
            // `continue :lbl;` exit / next-iterate it (possibly an OUTER loop) via a goto.
            case Zig.LabeledLoop ll:       return LowerLabeledLoop(Tok(ll.Arg0), ll.Arg2);
            case Zig.StmtBreakLabel b:     return LowerLabeledLoopJump(Tok(b.Arg2), isContinue: false);
            case Zig.StmtContinueLabel c:  return LowerLabeledLoopJump(Tok(c.Arg2), isContinue: true);

            // `inline for (lo..hi) |i| body` — comptime loop UNROLLING (Milestone T, part 3): replicate
            // the body once per index, with `i` bound to a compile-time constant in each copy.
            case Zig.InlineLoop il:        return LowerInlineLoop(il.Arg1);

            // `comptime var i = …;` — a compile-time value local (Milestone T, part 3). Tracked at
            // lowering time, no runtime decl; references substitute its current value (the `inline
            // while` counter).
            case Zig.ComptimeVarDecl cv:   return LowerComptimeVarDecl(cv.Arg1);

            // `comptime { … }` — a compile-time block statement (Milestone T, part 3): run the block's
            // comptime-value statements at lowering time, emit no runtime code.
            case Zig.ComptimeBlock cb:     return LowerComptimeBlock(cb.Arg1);

            // `switch (subject) { prongs }` → the C IR Switch (subject=Arg2, prongs=Arg5 for both
            // the plain and trailing-comma forms). A tagged-union subject takes the capture path.
            case Zig.StmtSwitch s:         return LowerSwitchStmt(s.Arg2, s.Arg5);
            case Zig.StmtSwitchTrailing s: return LowerSwitchStmt(s.Arg2, s.Arg5);
            case Zig.StmtSwitchSemi s:         return LowerSwitchStmt(s.Arg2, s.Arg5);
            case Zig.StmtSwitchTrailingSemi s: return LowerSwitchStmt(s.Arg2, s.Arg5);

            // `for (start..end) |i| body` → C `for (usize i = start; i < end; i++) body`. The
            // capture `i` is the usize loop index (its own scope so it doesn't leak); the end
            // is cast to usize so the comparison is unsigned-clean (C# forbids ulong<>signed).
            case Zig.StmtForRange f:
            {
                _symbols.EnterScope();
                var start = LowerExpr(f.Arg2);
                var end = LowerExpr(f.Arg4);
                var iSym = _symbols.Declare(new Symbol { Name = Tok(f.Arg7), Kind = SymKind.Var, Type = CType.ULong });
                var iRef = new VarRef(iSym) { Type = CType.ULong, IsLValue = true };
                var init = new DeclStmt(new List<LocalDecl> { new(iSym, start) });
                var cond = new Binary(BinOp.Lt, iRef, new Cast(CType.ULong, end) { Type = CType.ULong }) { Type = CType.Int };
                var post = new Unary(UnOp.PostInc, iRef) { Type = CType.ULong };
                var body = LowerStmt(f.Arg9);
                _symbols.ExitScope();
                return new For(init, cond, post, body);
            }

            // `for (s) |x| body` — iterate a slice's elements (x = a per-iteration copy).
            case Zig.StmtForSlice f:     // for '(' Expr ')' '|' IDENT '|' Stmt
                return LowerForSlice(LowerExpr(f.Arg2), Tok(f.Arg5), null, f.Arg7, byRef: false, DeclaredElemBitsOfValue(f.Arg2));
            // `for (s) |*x| body` — BY-REFERENCE element capture: x is a `*T` into the slice (Milestone M, part 4).
            case Zig.StmtForSliceRef f:  // for '(' Expr ')' '|' '*' IDENT '|' Stmt
                return LowerForSlice(LowerExpr(f.Arg2), Tok(f.Arg6), null, f.Arg8, byRef: true);
            // `for (s, 0..) |x, i| body` — also bind the usize index (counter + start).
            case Zig.StmtForSliceIdx f:  // for '(' Expr ',' Expr '..' ')' '|' IDENT ',' IDENT '|' Stmt
                return LowerForSlice(LowerExpr(f.Arg2), Tok(f.Arg8), (Tok(f.Arg10), LowerExpr(f.Arg4)), f.Arg12, byRef: false);
            // `for (a, b) |x, y| body` — the PARALLEL form (road-to-zig-std S6). Only the COMPTIME
            // form is lowered: a member list has no runtime representation, so the useful case is
            // always `inline for`. A runtime lockstep walk over two slices is a separate feature.
            // `for (a, b) |x, y|` / `for (a, b, c) |x, y, z|` at RUNTIME: a lockstep walk (road-to-zig-std
            // G5). The `inline for` over comptime member lists takes the parallel pair before this.
            case Zig.StmtForSlicePair f:
                return LowerForParallel(new[] { f.Arg2, f.Arg4 }, new[] { Tok(f.Arg7), Tok(f.Arg9) }, f.Arg11);
            case Zig.StmtForSlicePairTrail f:
                return LowerForParallel(new[] { f.Arg2, f.Arg4 }, new[] { Tok(f.Arg8), Tok(f.Arg10) }, f.Arg12);
            case Zig.StmtForSliceTriple f:
                return LowerForParallel(new[] { f.Arg2, f.Arg4, f.Arg6 }, new[] { Tok(f.Arg9), Tok(f.Arg11), Tok(f.Arg13) }, f.Arg15);
            case Zig.StmtForSliceTripleTrail f:
                return LowerForParallel(new[] { f.Arg2, f.Arg4, f.Arg6 }, new[] { Tok(f.Arg10), Tok(f.Arg12), Tok(f.Arg14) }, f.Arg16);
            case Zig.StmtForPairRefRef f:
                return LowerForParallel(new[] { f.Arg2, f.Arg4 }, new[] { Tok(f.Arg8), Tok(f.Arg11) }, f.Arg13, new[] { true, true });
            case Zig.StmtForPairRefVal f:
                return LowerForParallel(new[] { f.Arg2, f.Arg4 }, new[] { Tok(f.Arg8), Tok(f.Arg10) }, f.Arg12, new[] { true, false });
            case Zig.StmtForPairValRef f:
                return LowerForParallel(new[] { f.Arg2, f.Arg4 }, new[] { Tok(f.Arg7), Tok(f.Arg10) }, f.Arg12, new[] { false, true });
            // `for (s, 0..) |*x, i| body` — BY-REFERENCE element capture WITH the usize index
            // (Milestone Z): `x` is a `*T` into the slice (so `x.* = …` writes through), `i` the index.
            case Zig.StmtForSliceIdxRef f:  // for '(' Expr ',' Expr '..' ')' '|' '*' IDENT ',' IDENT '|' Stmt
                return LowerForSlice(LowerExpr(f.Arg2), Tok(f.Arg9), (Tok(f.Arg11), LowerExpr(f.Arg4)), f.Arg13, byRef: true);

            // A brace block in statement position (`Stmt -> Block`, pass-through).
            case Zig.Block:
            case Zig.BlockEmpty:        return LowerBlock(stmt);

            default: throw new IrUnsupportedException("zig statement: " + (stmt.Content?.GetType().Name ?? "null"));
        }
    }

    /// <summary>Lower a Zig compound assignment <c>target op= value</c> to the shared
    /// <see cref="Assign"/> node with a non-null <see cref="BinOp"/>. The C# backend renders a
    /// native <c>target op= value</c>, so the lvalue is evaluated EXACTLY ONCE — correct binding
    /// for a side-effecting lvalue like <c>a[i()] += 1</c> or <c>p.* += 1</c> (a textual
    /// <c>x = x op y</c> desugar would double-evaluate it). The RHS is sink-typed to the target
    /// type for parity with plain <see cref="Zig.StmtAssign"/> (harmless for a numeric RHS).</summary>
    private CStmt CompoundAssign(Item targetItem, BinOp op, Item valueItem)
        => TryAssignComptimeVar(targetItem, op, valueItem)
           ?? RejectConstStore(targetItem)
           ?? TryCompoundAssignOptionalPayload(targetItem, op, valueItem)
           ?? TryCompoundAssignValueControlFlow(targetItem, op, valueItem)
           // A hoist point, as a plain assignment is: `total += if (opt) |_| 100 else 2;` lowers its captured `if`
           // ahead of the statement.
           ?? Hoisted(() => new ExprStmt(CompoundAssignExpr(targetItem, op, valueItem)));

    /// <summary><c>total += switch (u) { .n =&gt; |v| v, … };</c> (task #109): a value <c>switch</c> / <c>if</c> / labeled block
    /// that needs statements (a capture or block prong) fills a temp at the target's type, as a plain assignment's
    /// does, and the compound operator then applies it. Null for any other right-hand side.</summary>
    private CStmt? TryCompoundAssignValueControlFlow(Item targetItem, BinOp op, Item valueItem)
    {
        var labeled = IsLabeledValue(valueItem);
        if (!labeled && !IsValueControlFlowStmt(valueItem)) { return null; }
        var target = LowerExpr(targetItem);
        CStmt Apply(Symbol temp)
        {
            var value = new VarRef(temp) { Type = temp.Type };
            if (op is BinOp.Div or BinOp.Mod) { CheckZigDivision(op, target, value); }
            return new ExprStmt(new Assign(op, target, value) { Type = target.Type });
        }
        return labeled ? LowerLabeledValue(valueItem, target.Type, Apply) : LowerValueControlFlowStmt(valueItem, target.Type, Apply);
    }

    /// <summary>A statement that is just <c>unreachable</c> or <c>comptime unreachable</c>.</summary>
    private static bool IsComptimeUnreachableStmt(Item stmt)
    {
        var e = stmt.Content is Zig.StmtExpr se ? se.Arg0 : stmt;
        if (e.Content is Zig.PreComptime pc) { e = pc.Arg1; }
        return e.Content is Zig.Ident { Arg0: var tok } && Tok(tok) == "unreachable";
    }

    /// <summary>An array expression that NAMES existing storage (a local, a field, an element): read as a value it
    /// must be copied, since its C# rep is the storage's element pointer.</summary>
    private static bool IsArrayLvalue(CExpr e) => e is VarRef or Member or DotCC.Ir.Index || e is Paren p && IsArrayLvalue(p.Inner);

    /// <summary>Declare <paramref name="sym"/> as a fresh <c>[N]T</c> local and copy <paramref name="source"/>'s elements
    /// into it.</summary>
    private static CStmt ArrayValueCopyDecl(Symbol sym, CType.Array arr, long count, CExpr source)
    {
        var countLit = new LitInt(count.ToString(CultureInfo.InvariantCulture), count) { Type = CType.Int };
        var target = new VarRef(sym) { Type = arr, IsLValue = true };
        return new Seq(new List<CStmt>
        {
            new ArrayDecl(sym, arr.Element, countLit, null),
            new ExprStmt(ArrayElementCopy(target, source, arr, count)),
        });
    }

    /// <summary><paramref name="count"/> elements of <paramref name="source"/> copied into <paramref name="target"/>
    /// (both arrays, rendered as their element pointers).</summary>
    private static CExpr ArrayElementCopy(CExpr target, CExpr source, CType.Array arr, long count)
    {
        var elem = arr.Element.Unqualified;
        var len = new LitInt(count.ToString(CultureInfo.InvariantCulture), count) { Type = CType.ULong };
        return new ZigMemCall("CopyForwards", elem, new List<CExpr>
        {
            new SliceNew(target, len, elem, false) { Type = new CType.Slice(elem) },
            new SliceNew(source, len, elem, true) { Type = new CType.Slice(elem) },
        }) { Type = CType.Void };
    }

    /// <summary>Whether an expression lowers to a slice (so its <c>.*</c> is the array it views), judged by lowering it
    /// into a throwaway buffer.</summary>
    private bool IsSliceOperand(Item item)
    {
        using var _ = EnterThrowawayHoist();
        try { return LowerExpr(item).Type?.Unqualified is CType.Slice; }
        catch (IrUnsupportedException) { return false; }
    }

    /// <summary><c>r.? *= 10;</c> (std.fmt.parseIntSizeSuffix): a value optional's payload is C#'s read-only
    /// <c>Nullable&lt;T&gt;.Value</c>, so the compound assignment writes the whole optional back:
    /// <c>r = (T?)(T)(r.Value * 10)</c> (a null <c>r</c> throws on the read, as zig's <c>.?</c> panics). Null for any
    /// other target. The optional is read twice, so only a plain variable qualifies.</summary>
    private CStmt? TryCompoundAssignOptionalPayload(Item targetItem, BinOp op, Item valueItem)
    {
        if (targetItem.Content is not Zig.Unwrap { Arg0: var optItem } || optItem.Content is not Zig.Ident) { return null; }
        var opt = LowerExpr(optItem);
        if (opt is not VarRef || opt.Type.Unqualified is not CType.Optional { Inner: var inner } optType) { return null; }
        var payload = new Member(opt, "Value", false) { Type = inner };
        var combined = new Cast(inner, new Binary(op, payload, LowerExprSink(valueItem, inner)) { Type = inner }) { Type = inner };
        return new ExprStmt(new Assign(null, opt, new Cast(optType, combined) { Type = optType }) { Type = optType });
    }

    /// <summary>An assignment to a <c>comptime var</c> (<c>i += 1;</c> in an unrolled <c>inline while</c>, std.Io.Writer
    /// .print's scan): executed NOW, at lowering time, updating the value later references substitute; it
    /// emits nothing. A runtime value is a loud error (zig rejects storing one into a comptime var). Null
    /// when the target is not a comptime var.</summary>
    private CStmt? TryAssignComptimeVar(Item targetItem, BinOp? op, Item valueItem)
    {
        if (targetItem.Content is not Zig.Ident id || _symbols.Resolve(Tok(id.Arg0)) is not { } sym) { return null; }
        // A comptime STRING var: `literal = literal ++ fmt[start..end];` folds to its new value.
        if (_comptimeStringVars.ContainsKey(sym))
        {
            if (op is not null || EvalComptimeValue(valueItem) is not LitStr newStr)
            {
                throw new IrUnsupportedException(
                    $"zig: `comptime var {sym.Name}` (a comptime string) can only be assigned a compile-time-known string");
            }
            _comptimeStringVars[sym] = newStr;
            return new Seq(new List<CStmt>());
        }
        if (!_comptimeVars.TryGetValue(sym, out var cur)) { return null; }
        CExpr value;
        using (EnterThrowawayHoist()) { value = LowerExpr(valueItem); }
        if (op is { } bop)
        {
            value = new Binary(bop, new LitInt(cur.Value.ToString(CultureInfo.InvariantCulture), cur.Value) { Type = cur.Type }, value)
            { Type = CType.Long };
        }
        if (_ir.ConstEval(value) is not { } next)
        {
            throw new IrUnsupportedException($"zig: `comptime var {sym.Name}` can only be assigned a compile-time-known value");
        }
        _comptimeVars[sym] = (next, cur.Type);
        return new Seq(new List<CStmt>());
    }

    /// <summary>The <c>Assign</c> CExpr for <c>target op= value</c> (a non-null <see cref="BinOp"/>) —
    /// the core shared by the statement form (wrapped in an <see cref="ExprStmt"/>) and the
    /// <c>while (…) : (i += 1)</c> continue-expression (used directly as the <see cref="For"/> post).</summary>
    private CExpr CompoundAssignExpr(Item targetItem, BinOp op, Item valueItem)
    {
        var target = LowerExpr(targetItem);
        // A shift's count is not the target's type (std.math.gcd's `x >>= @intCast(xz)`, task #86): a cast builtin there takes
        // C#'s `int` shift count.
        var value = op is BinOp.Shl or BinOp.Shr && valueItem.Content is Zig.BuiltinCall { Arg0: var shiftCast }
                    && Tok(shiftCast) is "@intCast" or "@truncate"
            ? LowerExprSink(valueItem, CType.Int)
            : LowerExprSink(valueItem, target.Type);
        // `a /= 2` / `a %= 3` follow the same signed-integer rule as `/` and `%` (task #98).
        if (op is BinOp.Div or BinOp.Mod) { CheckZigDivision(op, target, value); }
        return new Assign(op, target, value) { Type = target.Type };
    }

    /// <summary>The plain-<c>=</c> continue-expression post (<c>while (…) : (i = i + 1)</c>) — a
    /// null-op <see cref="Assign"/> CExpr, byte-identical to the legacy <c>=</c>-only form this
    /// widening replaced.</summary>
    private CExpr PlainAssignPost(Item lhsItem, Item rhsItem)
    {
        var lhs = LowerExpr(lhsItem);
        var rhs = LowerExpr(rhsItem);
        return new Assign(null, lhs, rhs) { Type = lhs.Type };
    }

    /// <summary>Build the <see cref="For"/>-post CExpr for a <c>while (…) : (lhs op rhs)</c>
    /// continue-expression, dispatching on the <c>AssignOp</c> operator node. Each arm mirrors the
    /// matching statement-level assignment case: plain <c>=</c> → a null-op Assign; a compound or
    /// wrapping op → an Assign carrying the same <see cref="BinOp"/> (wrap folds to the same node,
    /// like <c>stmtAddWrapAssign</c>); a saturating op → the <c>ZigMath.Sat…</c> clamp assignment.
    /// Shared by the plain (<see cref="Zig.StmtWhileContAssign"/>) and capture-while
    /// (<see cref="Zig.StmtWhileCaptureContAssign"/>) continue forms.</summary>
    /// <summary>A BLOCK continue expression (<c>while (c) : ({ a += 1; b += 1; }) body</c>) as the loop's
    /// update list: each statement lowers as it would anywhere, and must come out as a single expression
    /// statement (an assignment of any kind, a call), which becomes one item of the C#
    /// <c>for (;; a += 1, b += 1)</c> update clause (<see cref="CommaSeq"/>). A statement that needs more
    /// (a declaration, control flow, a hoisted temporary) is a loud cut.</summary>
    private CExpr ContBlockPost(Item blockItem)
    {
        var items = new List<CExpr>();
        IReadOnlyList<Item> stmts = blockItem.Content is Zig.Block b ? Flatten(b.Arg1) : [];
        foreach (var stmt in stmts)
        {
            var lowered = LowerStmt(stmt);
            while (lowered is Seq { Stmts.Count: 1 } single) { lowered = single.Stmts[0]; }
            if (lowered is not ExprStmt es)
            {
                throw new IrUnsupportedException(
                    "zig `while (…) : ({ … })`: the continue block may hold only assignments and calls (each "
                    + "becomes one item of the loop's update list); got " + (stmt.Content?.GetType().Name ?? "?"));
            }
            items.Add(es.Expr);
        }
        return items.Count == 1 ? items[0] : new CommaSeq(items) { Type = CType.Void };
    }

    private CExpr ContAssignPost(Item lhsItem, Item opItem, Item rhsItem) => opItem.Content switch
    {
        Zig.AopAssign  => PlainAssignPost(lhsItem, rhsItem),
        Zig.AopAdd     => CompoundAssignExpr(lhsItem, BinOp.Add, rhsItem),
        Zig.AopSub     => CompoundAssignExpr(lhsItem, BinOp.Sub, rhsItem),
        Zig.AopMul     => CompoundAssignExpr(lhsItem, BinOp.Mul, rhsItem),
        Zig.AopDiv     => CompoundAssignExpr(lhsItem, BinOp.Div, rhsItem),
        Zig.AopMod     => CompoundAssignExpr(lhsItem, BinOp.Mod, rhsItem),
        Zig.AopShl     => CompoundAssignExpr(lhsItem, BinOp.Shl, rhsItem),
        Zig.AopShr     => CompoundAssignExpr(lhsItem, BinOp.Shr, rhsItem),
        Zig.AopBitAnd  => CompoundAssignExpr(lhsItem, BinOp.BitAnd, rhsItem),
        Zig.AopBitOr   => CompoundAssignExpr(lhsItem, BinOp.BitOr, rhsItem),
        Zig.AopBitXor  => CompoundAssignExpr(lhsItem, BinOp.BitXor, rhsItem),
        // Wrapping ops fold to the same node as the plain compound: a native C# `target op= rhs`
        // already truncates to the LHS width in the unchecked context (two's-complement wrap).
        Zig.AopAddWrap => CompoundAssignExpr(lhsItem, BinOp.Add, rhsItem),
        Zig.AopSubWrap => CompoundAssignExpr(lhsItem, BinOp.Sub, rhsItem),
        Zig.AopMulWrap => CompoundAssignExpr(lhsItem, BinOp.Mul, rhsItem),
        Zig.AopAddSat  => SatCompoundAssignExpr(lhsItem, "SatAdd", rhsItem),
        Zig.AopSubSat  => SatCompoundAssignExpr(lhsItem, "SatSub", rhsItem),
        Zig.AopMulSat  => SatCompoundAssignExpr(lhsItem, "SatMul", rhsItem),
        _ => throw new IrUnsupportedException(
            $"unexpected while-continue assignment operator: {opItem.Content?.GetType().Name ?? "null"}"),
    };

    /// <summary>Lower a local <c>const</c> declaration, intercepting a comptime allocator /
    /// namespace binding first (Milestone F): <c>const std = @import("std");</c> /
    /// <c>const a = std.heap.page_allocator;</c> carry no runtime value, so they register the
    /// alias (<see cref="TryComptimeConstBinding"/>) and emit nothing (an empty <see cref="Seq"/>).
    /// Any other <c>const</c> is an ordinary <see cref="DeclOf"/>.</summary>
    /// <summary>Bind <c>const x = comptime E</c> whose value is a struct or array into the interpreter's
    /// comptime variables (see <see cref="DeclOrComptime"/>). False, with nothing bound, for any other value.</summary>
    private bool TryBindComptimeAggregateConst(Item nameTok, Item? typeItem, Item initExpr)
    {
        var declared = typeItem is { } ti ? LowerType(ti) : null;
        CExpr init;
        using (EnterThrowawayHoist()) { init = declared is { } dt ? LowerExprSink(initExpr, dt) : LowerExpr(initExpr); }
        if (init is not ComptimeFold { Resolved: StructInit or StackArray } || _ir.EvalComptimeValue(init) is not { } value)
        {
            return false;
        }
        var sym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = declared ?? init.Type });
        _ir.ComptimeGlobals[sym] = value;
        return true;
    }

    /// <summary>Bind <c>const x = f(T, U)</c>, a call whose every argument is a type and whose result struct is
    /// comptime-only (a <c>comptime_int</c> field), as a comptime aggregate (see <see cref="DeclOrComptime"/>). False, with nothing bound, for any
    /// other initializer, or when the call does not evaluate at compile time (then it is an ordinary runtime call).</summary>
    private bool TryBindTypeArgumentCallConst(Item nameTok, Item initExpr)
    {
        if (initExpr.Content is not Zig.CallArgs call || Flatten(call.Arg2) is not { Count: > 0 } args
            || !args.All(a => TryTypeAliasRhs(a, out _)))
        {
            return false;
        }
        CExpr inner;
        using (EnterThrowawayHoist())
        {
            try { inner = LowerExpr(initExpr); }
            catch (IrUnsupportedException) { return false; }
        }
        // Only a COMPTIME-ONLY struct (a `comptime_int` field, as std.fmt.parse_float's FloatInfo has): zig evaluates a call
        // returning one at compile time. Any other call stays a runtime call, side effects included.
        if (inner.Type?.Unqualified is not CType.Named { Name: var resultName }
            || _ir.StructFieldsOf(resultName) is not { } resultFields
            || !resultFields.Any(f => f.Type.Unqualified is CType.Prim { IsComptimeInt: true })
            || _ir.ResolveComptimeFold(inner) is not StructInit resolved)
        {
            return false;
        }
        var fold = new ComptimeFold(inner) { Type = inner.Type, Resolved = resolved };
        if (_ir.EvalComptimeValue(fold) is not { } value) { return false; }
        var sym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = inner.Type });
        _ir.ComptimeGlobals[sym] = value;
        return true;
    }

    private CStmt DeclOrComptime(Item nameTok, Item? typeItem, Item initExpr)
    {
        // `const add = switch (sign) { .pos => math.add, .neg => math.sub };` (std.fmt.parseIntWithSign):
        // a comptime alias of a FUNCTION, here a generic of another module picked by a comptime switch.
        // It has no runtime value to hold (a generic has no single address); a call through it
        // instantiates the function it names (see the bare-call path).
        if (typeItem is null && TryResolveFnAlias(initExpr) is { } fnAlias)
        {
            _fnAliases[Tok(nameTok)] = fnAlias;
            return new Seq(new List<CStmt>());
        }
        if (TryComptimeConstBinding(Tok(nameTok), initExpr)) { return new Seq(new List<CStmt>()); }
        // `const Scan = if (std.simd.suggestVectorLength(u8)) |vec_size| struct {…} else struct {…};` (std.mem.eqlBytes):
        // the comptime condition picks ONE struct, declared as a local container with the capture as its comptime value.
        if (typeItem is null && TryLowerSelectedLocalStruct(Tok(nameTok), initExpr) is { } selectedStruct) { return selectedStruct; }
        // `const placeholder = comptime std.fmt.Placeholder.parse(…);`: a comptime AGGREGATE lives in the
        // interpreter, like a `comptime var` of one (E3), so a later comptime read (`switch (placeholder.arg)`)
        // folds; a runtime read renders it where it stands.
        if (initExpr.Content is Zig.PreComptime && TryBindComptimeAggregateConst(nameTok, typeItem, initExpr))
        {
            return new Seq(new List<CStmt>());
        }
        // `const float_info = FloatInfo.from(T);` (std.fmt.parse_float): FloatInfo is comptime-only (its fields are
        // `comptime_int`), so zig evaluates the call at compile time without the `comptime` keyword; the struct binds as
        // the interpreter's aggregate, and `float_info.mantissa_explicit_bits + 3` can be a `comptime precision` argument.
        if (typeItem is null && TryBindTypeArgumentCallConst(nameTok, initExpr))
        {
            return new Seq(new List<CStmt>());
        }
        if (typeItem is null && TryBindComptimeOptionalSwitch(nameTok, initExpr)) { return new Seq(new List<CStmt>()); }
        // `const is_comptime = @TypeOf(x) == comptime_int;` (std.math.cast): a TYPE comparison (or a comptime
        // tag test) is a comptime bool with no runtime operands to hold, so it binds the folded literal.
        if (typeItem is null && TryFoldComptimeCondition(initExpr) is { } flag)
        {
            _comptimeValues[Tok(nameTok)] = new LitBool(flag) { Type = CType.Bool };
            return new Seq(new List<CStmt>());
        }
        // `const init_capacity: comptime_int = @max(1, std.atomic.cache_line / @sizeOf(T));` (array_list): a
        // comptime-only integer has no runtime type to hold it, so it folds and binds the literal.
        if (typeItem?.Content is Zig.Ident { Arg0: var ctTok } && Tok(ctTok) == "comptime_int")
        {
            if (ComptimeIntValue(LowerExprSink(initExpr, CType.Long)) is not { } ctValue)
            {
                throw new IrUnsupportedException(
                    $"zig `const {Tok(nameTok)}: comptime_int` must be initialized with a compile-time-known integer");
            }
            _comptimeValues[Tok(nameTok)] = new LitInt(ctValue.ToString(CultureInfo.InvariantCulture), ctValue) { Type = CType.Long };
            return new Seq(new List<CStmt>());
        }
        return DeclOf(nameTok, typeItem, initExpr, isConst: true);
    }

    /// <summary>A local <c>const NAME = if (c) struct {…} else struct {…};</c> or its captured form over a comptime
    /// optional: the condition folds, and the chosen struct is declared as a local container (with methods and
    /// consts) whose comptime value seeds include the capture (<c>vec_size</c>). Null for any other initializer; a
    /// condition that does not fold, or an enum arm, is loud.</summary>
    private CStmt? TryLowerSelectedLocalStruct(string name, Item initExpr)
    {
        Item arm;
        var extraSeeds = new List<(string name, long value, CType type)>();
        switch (initExpr.Content)
        {
            case Zig.IfExprTypeArms ta:
                arm = (TryFoldComptimeCondition(ta.Arg2) ?? (_ir.ConstEval(LowerExpr(ta.Arg2)) is { } cv ? cv != 0 : (bool?)null))
                      switch
                {
                    true => ta.Arg4,
                    false => ta.Arg6,
                    null => throw new IrUnsupportedException($"zig: `const {name} = if (…) struct {{…}} else …` needs a comptime condition"),
                };
                break;
            case Zig.IfExprCaptureTypeArms ca:
                if (!TryComptimeOptionalCond(ca.Arg2, out var copt))
                {
                    throw new IrUnsupportedException(
                        $"zig: `const {name} = if (x) |v| struct {{…}} else …` needs a comptime-known optional");
                }
                if (copt.HasValue)
                {
                    arm = ca.Arg7;
                    extraSeeds.Add((Tok(ca.Arg5), copt.Value, copt.Inner));
                }
                else
                {
                    arm = ca.Arg9;
                }
                break;
            default:
                return null;
        }
        if (arm.Content is not Zig.TypeArmStruct selected)
        {
            throw new IrUnsupportedException($"zig: `const {name} = if (…) …` selects a non-struct type arm, which is not lowered yet");
        }
        return LowerLocalStruct(name, selected.Arg2, AggregateLayout.Default, extraSeeds);
    }

    /// <summary>Bind <c>const arg_pos = comptime switch (placeholder.arg) { .none =&gt; null, .number =&gt; |pos| pos, … };</c>
    /// (std.Io.Writer.print) as a comptime OPTIONAL: a switch over a comptime subject with a <c>null</c> prong is
    /// zig's <c>?T</c>, and the selected prong is either that <c>null</c> or a compile-time integer. Its later
    /// reads (<c>arg_state.nextArg(arg_pos)</c>, run by the interpreter) then see a constant. False when the
    /// switch has no <c>null</c> prong, its subject is not comptime-known, or the payload does not fold.</summary>
    private bool TryBindComptimeOptionalSwitch(Item nameTok, Item initExpr)
    {
        var rhs = initExpr.Content is Zig.ComptimeSwitchExpr cs ? cs.Arg1 : initExpr;
        var (subject, prongsItem) = rhs.Content switch
        {
            Zig.SwitchExpr s => (s.Arg2, s.Arg5),
            Zig.SwitchExprTrailing s => (s.Arg2, s.Arg5),
            _ => ((Item?)null, (Item?)null),
        };
        if (subject is null || prongsItem is null) { return false; }
        if (!Flatten(prongsItem).Any(p => DecomposeProng(p).Expr?.Content is Zig.NullLit)) { return false; }
        if (SelectComptimeProng(subject, prongsItem, out var payload) is not { Expr: { } value } prong) { return false; }
        bool hasValue;
        long v = 0;
        CType inner = CType.ULong;
        if (value.Content is Zig.NullLit)
        {
            hasValue = false;
        }
        else
        {
            EnterComptimeProng(prong, payload);
            try
            {
                CExpr lowered;
                using (EnterThrowawayHoist()) { lowered = LowerExpr(value); }
                if (_ir.ConstEval(lowered) is not { } folded) { return false; }
                hasValue = true;
                v = folded;
                if (lowered.Type?.Unqualified is CType.Prim { Integer: true, IsComptimeInt: false, Name: not "_Bool" } t) { inner = t; }
            }
            finally { ExitComptimeProng(); }
        }
        var sym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = new CType.Optional(inner) });
        _comptimeOptionalVars[sym] = (hasValue, v, inner);
        return true;
    }

    /// <summary>Local comptime aliases of a function (see <see cref="DeclOrComptime"/>): name → the
    /// module that owns the function and its symbol. Function-flat, like the other comptime bindings.</summary>
    private readonly Dictionary<string, (ZigLowering Owner, Symbol Sym)> _fnAliases = new(System.StringComparer.Ordinal);

    /// <summary>The function a comptime <c>const</c> initializer names, or null: a module-qualified
    /// GENERIC function (<c>math.add</c>), or a <c>switch</c> / <c>if</c> whose comptime-known subject
    /// selects an arm that names one. A non-generic function is left to the ordinary path, where it is a
    /// fn-pointer value.</summary>
    private (ZigLowering Owner, Symbol Sym)? TryResolveFnAlias(Item rhs)
    {
        switch (rhs.Content)
        {
            case Zig.Grouped g:
                return TryResolveFnAlias(g.Arg1);
            case Zig.Field f when ResolveModulePath(f.Arg0)?.Lowering is { } owner
                               && owner.ResolveExportedDecl(Tok(f.Arg2)) is { } decl
                               && decl.Sym.Kind == SymKind.Func && decl.Owner.IsGenericTemplate(decl.Sym):
                return decl;
            case Zig.SwitchExpr or Zig.SwitchExprTrailing:
            {
                var (subjectItem, prongsItem) = rhs.Content switch
                {
                    Zig.SwitchExpr s => (s.Arg2, s.Arg5),
                    Zig.SwitchExprTrailing s => (s.Arg2, s.Arg5),
                    _ => throw new System.InvalidOperationException(),
                };
                if (!LooksLikeFnAliasArms(prongsItem)) { return null; }
                CExpr subject;
                using (EnterThrowawayHoist()) { subject = LowerExpr(subjectItem); }
                if (_ir.ConstEval(subject) is not { } v) { return null; }
                Item? elseArm = null;
                foreach (var prong in Flatten(prongsItem))
                {
                    if (prong.Content is not Zig.ProngExpr pe) { return null; }
                    if (pe.Arg0.Content is Zig.CaseElse) { elseArm = pe.Arg2; continue; }
                    foreach (var label in LowerCaseVals(pe.Arg0, subject.Type))
                    {
                        if (label.HiExpr is null && label.CaseExpr is { } ce && _ir.ConstEval(ce) == v)
                        {
                            return TryResolveFnAlias(pe.Arg2);
                        }
                    }
                }
                return elseArm is { } ea ? TryResolveFnAlias(ea) : null;
            }
            case Zig.IfExpr e:
            {
                // std/sort/block.zig: `if (builtin.mode == .Debug) struct { … }.lessThan else lessThanFn`.
                if (TryFoldComptimeCondition(e.Arg2) is { } taken) { return TryResolveFnAlias(taken ? e.Arg4 : e.Arg6); }
                CExpr cond;
                using (EnterThrowawayHoist()) { cond = LowerExpr(e.Arg2); }
                return _ir.ConstEval(cond) is { } c ? TryResolveFnAlias(c != 0 ? e.Arg4 : e.Arg6) : null;
            }
            // An arm naming a comptime FUNCTION parameter (`else lessThanFn`), or a closure-idiom method.
            case Zig.Ident fid when _fnAliases.TryGetValue(Tok(fid.Arg0), out var passed):
                return passed;
            case Zig.StructMemberExpr sme:
                return (this, ReifyClosureExpr(rhs, sme));
            default:
                return null;
        }
    }

    /// <summary>Reify a closure-idiom struct in EXPRESSION position (<c>struct { fn f … }.f</c>) and return
    /// the method, memoized per source site. Its method bodies drain without the enclosing instance's
    /// comptime seeds (a V1 cut the only std site, block.zig's Debug-mode arm, never reaches in the
    /// ReleaseFast mode dotcc reports).</summary>
    private Symbol ReifyClosureExpr(Item site, Zig.StructMemberExpr sme)
    {
        if (_closureSites.TryGetValue(site, out var known)) { return known; }
        var owner = $"{_currentFnName}__L{_closureSites.Count}";
        var sym = ReifyClosureStruct(owner, sme.Arg2, Tok(sme.Arg5),
            System.Array.Empty<TypeSeed>(), System.Array.Empty<(string, long, CType)>(),
            System.Array.Empty<(string, bool, long, CType)>());
        _closureSites[site] = sym;
        return sym;
    }

    /// <summary>Each closure-idiom expression site → its reified method (<see cref="ReifyClosureExpr"/>).</summary>
    private readonly Dictionary<Item, Symbol> _closureSites = new(ReferenceEqualityComparer.Instance);

    /// <summary>True when every arm of a switch is a bare dotted path (<c>.pos =&gt; math.add</c>): the only
    /// shape <see cref="TryResolveFnAlias"/> evaluates, so an ordinary value switch is never lowered twice.</summary>
    private static bool LooksLikeFnAliasArms(Item prongsItem)
        => Flatten(prongsItem).All(p => p.Content is Zig.ProngExpr { Arg2.Content: Zig.Field });

    // `const`/`var x = init;` — lower under an ANF hoist buffer so a catch/orelse in a SUB-expression
    // of the initializer (`const r = 1 + (a catch b());`) lifts to a temp before the decl. A
    // WHOLE-init catch / control-flow fallback is intercepted at the top of DeclOfInner (its own
    // statement lowering), leaving the buffer empty, so this wrap is a no-op for those.
    private CStmt DeclOf(Item nameTok, Item? typeItem, Item initExpr, bool isConst = false)
    {
        var before = _symbols.Resolve(Tok(nameTok));
        var decl = Hoisted(() => DeclOfInner(nameTok, typeItem, initExpr, isConst));
        // A `const` local is immutable: a later store to it is zig's "cannot assign to constant" (task #95).
        if (isConst && _symbols.Resolve(Tok(nameTok)) is { } declared && !ReferenceEquals(declared, before))
        {
            _zigConstBindings.Add(declared);
        }
        return decl;
    }

    private CStmt DeclOfInner(Item nameTok, Item? typeItem, Item initExpr, bool isConst)
    {
        // Compute the declared type FIRST: a result-located init (`.member` / `.{…}`) needs
        // it as its sink, so resolve the annotation before lowering the initializer.
        var declared = typeItem is not null ? LowerType(typeItem) : null;
        // `const x = blk: { … break :blk v; };` — a labeled value-block initializer. Temp-fill it
        // (the declared type, if any, is the sink), then bind `x` to the result temp.
        if (IsLabeledValue(initExpr))
        {
            return LowerLabeledValue(initExpr, declared, temp =>
            {
                var sym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = temp.Type });
                return new DeclStmt(new List<LocalDecl> { new(sym, new VarRef(temp) { Type = temp.Type }) });
            });
        }
        // `const x = switch (y) { … blk: {…} };` / `const x = if (c) blk:{…} else …;` — a value-
        // position if/switch with a labeled-block (statement-producing) branch (Milestone Y, part 1):
        // temp-fill it as a statement (the declared type, if any, is the sink), then bind `x` to the
        // result temp. An all-simple-value if/switch is NOT intercepted here (it stays the clean C#
        // ternary / switch-expression).
        if (IsValueControlFlowStmt(initExpr))
        {
            return LowerValueControlFlowStmt(initExpr, declared, temp =>
            {
                var sym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = temp.Type });
                return new DeclStmt(new List<LocalDecl> { new(sym, new VarRef(temp) { Type = temp.Type }) });
            });
        }
        // `const v = a catch return [x];` / `const v = a orelse return [x];` (Milestone N, part 6) —
        // a control-flow fallback. On the error/none path the `return` runs (early-out); on success
        // `v` binds the unwrapped payload.
        if (IsControlFlowFallback(initExpr, out var cfLhs, out var cfCatch, out var cfCap, out var cfArm))
        {
            return LowerControlFlowFallback(cfLhs, cfCatch, cfCap, cfArm, payload =>
            {
                var ptype = declared ?? payload.Type ?? CType.Int;
                var psym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = ptype });
                return new DeclStmt(new List<LocalDecl> { new(psym, payload) });
            });
        }
        // `const v = a catch |e| b;` / `const v = a catch <side-effecting>;` (Milestone N, part 3) —
        // a capturing or side-effecting catch needs a statement context (the fallback runs only on
        // error; the capture binds `e`). Hoist + (bind) + initialize `v` from the lazy ternary. A
        // simple, side-effect-free `a catch b` (no capture) yields empty pre and falls through to the
        // normal path below — the eager `ErrUnion.Catch`, unchanged.
        if (initExpr.Content is Zig.CatchOp or Zig.CatchCapture)
        {
            string? capName = initExpr.Content is Zig.CatchCapture cc ? Tok(cc.Arg3) : null;
            var unionIt = initExpr.Content switch { Zig.CatchOp co => co.Arg0, Zig.CatchCapture c2 => c2.Arg0, _ => initExpr };
            var fbIt = initExpr.Content switch { Zig.CatchOp co => co.Arg2, Zig.CatchCapture c2 => c2.Arg5, _ => initExpr };
            var (pre, value) = LowerCatchValue(unionIt, capName, fbIt);
            // Capture always lowers structurally; a no-capture catch only when it hoisted (i.e. the
            // fallback was side-effecting). A simple no-capture catch (empty pre) falls through.
            if (capName is not null || pre.Count > 0)
            {
                var ctype = declared ?? value.Type ?? CType.Int;
                var csym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = ctype });
                pre.Add(new DeclStmt(new List<LocalDecl> { new(csym, value) }));
                return pre.Count == 1 ? pre[0] : new Seq(pre);
            }
        }
        // `var b: [N]T = …;` → a stackalloc'd C array (ArrayDecl → `T* b = stackalloc T[…]`), so
        // `b[i]` / `b[lo..hi]` reuse the array paths and yield a stack-backed slice. `undefined`
        // gives a zeroed extent; an array literal (`.{…}` / `[N]T{…}`, Milestone K) gives a
        // stackalloc with the element inits. The literal lowers BEFORE the symbol is declared, so
        // the array name isn't visible in its own initializer.
        if (declared is CType.Array arr)
        {
            // `[N:s]T` sentinel array (part 4; non-zero sentinel in Milestone Z): reserve ONE extra
            // trailing slot for the sentinel. The symbol keeps the logical `CType.Array(element, N)`
            // type (so `.len` / slicing exclude the sentinel); only the stackalloc extent (and the
            // literal's element list) grow by one. A ZERO sentinel rides C#'s zero-fill; a NON-ZERO
            // sentinel is written into the trailing slot explicitly.
            var sentinel = IsSentinelArrayType(typeItem);
            var sentVal = sentinel ? SentinelArrayValue(typeItem) : 0;
            if (initExpr.Content is Zig.UndefinedLit)
            {
                var n = (arr.Count ?? 0) + (sentinel ? 1 : 0);
                var sym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = arr });
                var count = new LitInt(n.ToString(CultureInfo.InvariantCulture), n) { Type = CType.Int };
                var decl = new ArrayDecl(sym, arr.Element, count, null);   // C# zero-fills the stackalloc
                if (sentinel && sentVal != 0)
                {
                    // Zero-fill left the trailing slot at 0; write the actual non-zero sentinel there.
                    var nIdx = arr.Count ?? 0;
                    var slot = new DotCC.Ir.Index(new VarRef(sym) { Type = arr, IsLValue = true },
                        new LitInt(nIdx.ToString(CultureInfo.InvariantCulture), nIdx) { Type = CType.ULong })
                        { Type = arr.Element, IsLValue = true };
                    var write = new ExprStmt(new Assign(null, slot,
                        new LitInt(sentVal.ToString(CultureInfo.InvariantCulture), sentVal) { Type = CType.Int })
                        { Type = arr.Element });
                    return new Seq(new List<CStmt> { decl, write });
                }
                return decl;
            }
            var arrInit = LowerExprSink(initExpr, arr);
            // A `comptime EXPR` initializer is a ComptimeFold until pass 3 resolves it to a
            // StackArray (e.g. `const t: [N]T = comptime buildTable();`). Route it through the
            // ordinary DeclStmt path — the symbol is array-typed (renders `T*`), and the backend
            // hoists the resolved StackArray into `T* t = stackalloc T[]{…}` exactly as the
            // inferred-type form does. (A sentinel `[N:0]T` would need the +1 stackalloc slot, which
            // this path can't add, so a comptime sentinel array stays a clear error below.)
            // A CALL returning `[N]T` (std.mem.reverse's `const left_shuffled: [simd_size]T = reverseVector(…)`)
            // hands back a fresh copy the caller owns (ZigAlloc.CopyArrayResult), so binding it keeps zig's
            // by-value semantics, as the inferred `const t = f();` form already does.
            // `const c: [3]u8 = a;`: another array's VALUE, so the local gets its own storage and a copy.
            if (IsArrayLvalue(arrInit) && !sentinel && arr.Count is { } copyCount)
            {
                var csym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = arr });
                return ArrayValueCopyDecl(csym, arr, copyCount, arrInit);
            }
            if ((arrInit is ComptimeFold || arrInit is Call { Type: CType.Array }) && !sentinel)
            {
                var fsym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = arr });
                return new DeclStmt(new List<LocalDecl> { new(fsym, arrInit) });
            }
            if (arrInit is not StackArray sa)
            {
                throw new IrUnsupportedException(
                    $"a `[N]T` array local '{Tok(nameTok)}' must be initialized with an array literal (`.{{…}}` / `[N]T{{…}}`) or `undefined`");
            }
            var asym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = arr });
            // Append the trailing sentinel → `stackalloc T[]{ e0, …, eN-1, s }` lays down N+1 slots.
            // The sentinel is an `int` literal (NOT element-typed): it renders bare (e.g. `0` / `5`),
            // which C#'s constant conversion accepts into any element type — an element-typed literal
            // on an unsigned/narrow element would render `0u`/`5u` and fail the implicit byte conversion.
            var elems = sentinel
                ? new List<CExpr>(sa.Elems) { new LitInt(sentVal.ToString(CultureInfo.InvariantCulture), sentVal) { Type = CType.Int } }
                : sa.Elems;
            var countLit = new LitInt(elems.Count.ToString(CultureInfo.InvariantCulture), elems.Count) { Type = CType.Int };
            return new ArrayDecl(asym, sa.Element, countLit, elems);
        }
        var init = LowerExprSink(initExpr, declared);
        // `const t = x > 2;` is a zig `bool`, though the IR types a comparison as C's `int` (task #81): `{}` prints it
        // `true`, and `@TypeOf(t)` is `bool`.
        var type = declared ?? (IsZigBoolValue(init) ? CType.Bool : init.Type) ?? CType.Int;
        // `var b = a;` of an array local: zig arrays are VALUES, so `b` is a copy, not a second name for `a`'s
        // storage (the C# rep of an array local is its element pointer, which a plain decl would share).
        if (declared is null && type.Unqualified is CType.Array { Count: { } untypedCount } untypedArr && IsArrayLvalue(init))
        {
            var usym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = untypedArr });
            return ArrayValueCopyDecl(usym, untypedArr, untypedCount, init);
        }
        // A local `const` whose integer initializer folds IS that value in every comptime question, as a
        // top-level one is (and as zig has it): `const max_format_args = @typeInfo(ArgSetType).int.bits;`
        // makes std.Io.Writer.print's `if (field_names.len > max_format_args) @compileError(…)` fold.
        var folded = isConst && type.Unqualified is CType.Prim { Integer: true } ? _ir.ConstEval(init) : null;
        var sym2 = _symbols.Declare(new Symbol
        {
            Name = Tok(nameTok), Kind = SymKind.Var, Type = type,
            IsConstexpr = folded is not null, ConstValue = folded ?? 0,
        });
        if (declared is null && init is LitStr) { _stringLiteralSyms.Add(sym2); }
        if (isConst && declared is null && init is LitStr && ComptimeStringArg(initExpr) is { } constText) { _constStringLocals[sym2] = constText; }
        // `const value = 42;` is a comptime_int in zig (the lowered local is an `int` carrier): an `anytype` it is
        // passed to binds it as a comptime value (ComptimeIntArgValue).
        if (declared is null && isConst && folded is not null && initExpr.Content is Zig.IntLit) { _comptimeIntLocals.Add(sym2); }
        // A `comptime_int` const whose initializer is a CALL (std.sort.pdq's `const stack_size = math.log2(math.maxInt(usize) + 1);`)
        // does not fold here; an array extent that names it runs the call then (task #73, see ConstEvalArraySize). Only a
        // comptime_int: zig rejects a runtime-typed call result (`const n = f(3);`) as an extent, and so does dotcc.
        // A comptime-only call's fold (`ComptimeFold`, its result carried as an Int128) is a comptime_int as well.
        if (isConst && folded is null && (init.Type?.Unqualified is CType.Prim { IsComptimeInt: true } || init is ComptimeFold))
        {
            _unfoldedConstInits[sym2] = init;
        }
        RecordValueBits(sym2,
            typeItem is { } ti ? DeclaredBitsOfTypeArg(ti) : DeclaredBitsOfValue(initExpr) ?? DeclaredBitsOfLowered(init),
            typeItem is { } te ? ElemBitsOfTypeAst(te) : DeclaredElemBitsOfValue(initExpr));
        // A `void` local (`var unit: void = {};`) has no storage and no C# spelling: the name stays
        // declared, so a use of it is an (erasable) void read, and the declaration emits nothing.
        if (type.Unqualified is CType.VoidType && IsErasableVoid(init)) { return new Seq(new List<CStmt>()); }
        return new DeclStmt(new List<LocalDecl> { new(sym2, init) });
    }

    /// <summary>Lower a destructure binding <c>&lt;binder&gt;, &lt;binder&gt;… = e;</c> (Milestone G,
    /// extended in S). A binder is a fresh <c>const</c>/<c>var</c> (optionally typed <c>: T</c>), an
    /// existing lvalue, or a <c>_</c> discard. Two lowerings, picked by the RHS shape:
    /// <list type="bullet">
    /// <item>A tuple-LITERAL RHS (<c>.{e0, e1, …}</c>) is lowered ELEMENT-WISE in source order with NO
    /// snapshot temp — each element <c>e_i</c> is bound/assigned directly. This matches Zig's
    /// sequential destructuring, where an existing-lvalue write is visible to a LATER element's read
    /// (so <c>a, b = .{ b, a }</c> is NOT a swap: <c>a←b</c>, then <c>b←</c> the new <c>a</c>). New
    /// binders can't alias (Zig forbids shadowing), so the order is also faithful for them, and a typed
    /// binder drives its element's result location (sink).</item>
    /// <item>A non-literal tuple-valued RHS (a fn call, a tuple var) is evaluated ONCE into a fresh
    /// <c>__tupN</c> temp, then each binder reads its positional element (<c>__tupN.ItemK</c>) — single
    /// eval, and a value temp can't alias an lvalue being written.</item>
    /// </list>
    /// Emitted as a brace-less <see cref="Seq"/> so any new binders land in the ENCLOSING scope (a
    /// <see cref="Block"/> would wrongly scope them). The arity must match the binder count.</summary>
    private CStmt LowerDestructure(Zig.StmtDestructure d)
    {
        // Binders in source order: the leading one (Arg0) + the rest (the Arg2 list).
        var binders = new List<Item> { d.Arg0 };
        binders.AddRange(Flatten(d.Arg2));
        var stmts = new List<CStmt>();

        // RhsExpr is transparent, so d.Arg4.Content is the underlying literal/expr directly.
        if (IsPositionalTupleLiteral(d.Arg4, out var elemItems))
        {
            if (elemItems.Count != binders.Count)
            {
                throw new IrUnsupportedException(
                    $"zig destructure binds {binders.Count} name(s) but the literal has {elemItems.Count} element(s)");
            }
            // Element-wise, source order: each binder lowers its own element expr (a typed/lvalue
            // binder passes its type as the element's sink). No temp — preserves Zig's aliasing.
            for (int i = 0; i < binders.Count; i++)
            {
                stmts.Add(LowerDestructBinder(binders[i], elemItems[i], snapshotRead: null));
            }
            return new Seq(stmts);
        }

        var rhs = LowerExpr(d.Arg4);
        if (rhs.Type.Unqualified is not CType.Tuple tup)
        {
            throw new IrUnsupportedException(
                $"zig destructure `…, … = e` needs a tuple value; got {rhs.Type.Describe()}");
        }
        if (tup.Elements.Count != binders.Count)
        {
            throw new IrUnsupportedException(
                $"zig destructure binds {binders.Count} name(s) but the tuple has {tup.Elements.Count} element(s)");
        }
        // The single-eval temp: `var __tupN = e;`, then each binder reads `__tupN.ItemK`.
        var tmp = _symbols.Declare(new Symbol { Name = "__tup" + _tupleTempCounter++, Kind = SymKind.Var, Type = tup });
        stmts.Add(new DeclStmt(new List<LocalDecl> { new(tmp, rhs) }));
        var tmpRef = new VarRef(tmp) { Type = tup, IsLValue = true };
        for (int i = 0; i < binders.Count; i++)
        {
            var et = tup.Elements[i];
            var read = new TupleIndex(tmpRef, i, et) { Type = et };
            stmts.Add(LowerDestructBinder(binders[i], elemItem: null, snapshotRead: read));
        }
        return new Seq(stmts);
    }

    /// <summary>Emit one destructure binder's statement (Milestone G + S). A fresh <c>const</c>/<c>var</c>
    /// binder (optionally typed <c>: T</c>) declares a local; an existing-lvalue binder assigns through
    /// it; <c>_</c> discards. The source value is either the tuple-literal element <paramref name="elemItem"/>
    /// (lowered at the binder's declared/lvalue type as its sink) or the snapshot read
    /// <paramref name="snapshotRead"/> (coerced to the binder's type). Exactly one of the two is non-null.</summary>
    private CStmt LowerDestructBinder(Item binder, Item? elemItem, CExpr? snapshotRead)
    {
        switch (binder.Content)
        {
            case Zig.DestructBindConst c:      return DeclareDestructLocal(Tok(c.Arg1), null, elemItem, snapshotRead);
            case Zig.DestructBindVar v:        return DeclareDestructLocal(Tok(v.Arg1), null, elemItem, snapshotRead);
            case Zig.DestructBindConstTyped c: return DeclareDestructLocal(Tok(c.Arg1), LowerType(c.Arg3), elemItem, snapshotRead);
            case Zig.DestructBindVarTyped v:   return DeclareDestructLocal(Tok(v.Arg1), LowerType(v.Arg3), elemItem, snapshotRead);
            case Zig.DestructBindLValue lv:    return AssignDestructTarget(lv.Arg0, elemItem, snapshotRead);
            default:
                throw new IrUnsupportedException(
                    "zig destructure binder: " + (binder.Content?.GetType().Name ?? "null"));
        }
    }

    /// <summary>Declare a fresh destructure local <c>name</c>. With a <paramref name="declType"/> the
    /// element lowers at that type as its sink (literal RHS) or the snapshot read is coerced to it;
    /// without one the type is inferred from the element/read.</summary>
    private CStmt DeclareDestructLocal(string name, CType? declType, Item? elemItem, CExpr? snapshotRead)
    {
        CExpr value = elemItem is not null
            ? (declType is not null ? LowerExprSink(elemItem, declType) : LowerExpr(elemItem))
            : (declType is not null ? CoerceRead(snapshotRead!, declType) : snapshotRead!);
        var symType = declType ?? value.Type;
        var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = symType });
        return new DeclStmt(new List<LocalDecl> { new(sym, value) });
    }

    /// <summary>Assign a destructure element to an existing lvalue (or discard it for <c>_</c>). The
    /// lvalue is the sink for a literal element; a snapshot read is coerced to the lvalue type. A
    /// <c>_</c> binder just evaluates the element for its side effects (a value-context discard).</summary>
    private CStmt AssignDestructTarget(Item lvalueItem, Item? elemItem, CExpr? snapshotRead)
    {
        if (lvalueItem.Content is Zig.Ident id && Tok(id.Arg0) == "_")
        {
            // `_` — evaluate the element/read; ExprStmt renders a `_ = …` discard when it isn't a call.
            return new ExprStmt(elemItem is not null ? LowerExpr(elemItem) : snapshotRead!);
        }
        var target = LowerExpr(lvalueItem);
        CExpr value = elemItem is not null
            ? LowerExprSink(elemItem, target.Type)
            : CoerceRead(snapshotRead!, target.Type);
        return new ExprStmt(new Assign(null, target, value) { Type = target.Type });
    }

    /// <summary>Coerce a snapshot read (a <c>__tupN.ItemK</c> CExpr) to a binder's declared/lvalue
    /// type, inserting a <see cref="Cast"/> only when the types differ (a no-op when they match).</summary>
    private static CExpr CoerceRead(CExpr read, CType to)
        => read.Type.Unqualified.Equals(to.Unqualified) ? read : new Cast(to, read) { Type = to };

    /// <summary>True when <paramref name="rhsItem"/> is a positional tuple literal (<c>.{e0, e1, …}</c>),
    /// yielding its element expressions in <paramref name="elemItems"/>. A named <c>.{.f = v}</c> (a
    /// struct literal) or the empty <c>.{}</c> is not a positional tuple literal → false (the snapshot
    /// path then handles / rejects it).</summary>
    private static bool IsPositionalTupleLiteral(Item rhsItem, out IReadOnlyList<Item> elemItems)
    {
        elemItems = [];
        if (rhsItem.Content is not Zig.AnonStructInit a) { return false; }
        var fields = Flatten(a.Arg2);
        if (fields.Count == 0) { return false; }
        var items = new List<Item>(fields.Count);
        foreach (var f in fields)
        {
            if (f.Content is not Zig.FieldInitPositional pos) { return false; }   // a named field → struct literal
            items.Add(pos.Arg0);
        }
        elemItems = items;
        return true;
    }

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
        Zig.LabeledSwitch { Arg2.Content: Zig.SwitchExpr sw } ls => LowerLabeledValueBody(Tok(ls.Arg0), () => LowerLabeledSwitchBody(Tok(ls.Arg0), sw.Arg2, sw.Arg5), sink, consume),
        Zig.LabeledSwitch { Arg2.Content: Zig.SwitchExprTrailing st } ls => LowerLabeledValueBody(Tok(ls.Arg0), () => LowerLabeledSwitchBody(Tok(ls.Arg0), st.Arg2, st.Arg5), sink, consume),
        _ => throw new IrUnsupportedException("internal: not a labeled value: " + (labeled.Content?.GetType().Name ?? "null")),
    };

    /// <summary>A labeled switch's body: the switch as a statement, its bare value prongs breaking to <paramref name="label"/>.</summary>
    private CStmt LowerLabeledSwitchBody(string label, Item subjectItem, Item prongsItem)
    {
        _pendingSwitchValueLabel = label;
        return LowerSwitchStmt(subjectItem, prongsItem);
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
        if (valueItem.Content is Zig.SwitchExpr or Zig.SwitchExprTrailing
            && (valueItem.Content is Zig.SwitchExpr bs ? bs.Arg5 : ((Zig.SwitchExprTrailing)valueItem.Content).Arg5) is var breakProngs
            && Flatten(breakProngs).Any(p => p.Content is not Zig.ProngExpr))
        {
            var breakSubject = valueItem.Content is Zig.SwitchExpr bss ? bss.Arg2 : ((Zig.SwitchExprTrailing)valueItem.Content).Arg2;
            return LowerLabeledSwitchBody(label, breakSubject, breakProngs);
        }
        // A labeled value-position loop (`lbl: while/for … else`, Milestone Y part 2) — innermost-first.
        foreach (var lv in _loopValues)
        {
            if (lv.Label == label) { return BuildLoopBreakValue(lv, valueItem); }
        }
        // A value break targeting a labeled STATEMENT loop (no `else` → not a value loop) is still a
        // clear deferred error — and is invalid Zig anyway (a value `break` needs a value loop).
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

    /// The largest number of iterations <c>inline for</c> will unroll — a backstop on an absurd
    /// <c>inline for (0..1_000_000)</c> (each iteration emits a full body copy, so the cap is far
    /// tighter than the comptime-array cap). A real <c>inline</c> loop is a handful to a few dozen.
    private const long InlineUnrollCap = 1 << 12;   // 4096

    /// <summary>Lower an <c>inline for</c> (Milestone T, part 3) by UNROLLING: the body is replicated
    /// once per iteration, each copy a block <c>{ const cap = …; body }</c> binding the capture to that
    /// iteration's value. The loop vanishes — no runtime <c>for</c> — and because each copy is plain
    /// straight-line IR, it works identically whether the enclosing function runs at runtime (the
    /// copies execute in order) or is itself <c>comptime</c>-called (the interpreter walks the unrolled
    /// copies, the per-copy binding entering its frame). Two forms are unrolled:
    /// <list type="bullet">
    /// <item><c>for (lo..hi) |i|</c> — the COUNTED range; the bounds fold to compile-time constants
    /// and the capture binds to each constant index.</item>
    /// <item><c>for (arr) |x|</c> — over a fixed <c>[N]T</c> array of comptime-known length; the
    /// capture binds to each element by value (<c>const x = arr[k];</c>).</item>
    /// </list>
    /// An <c>inline while</c>, an <c>inline for</c> over a slice (length not comptime-known) or the
    /// indexed <c>|x, i|</c> / by-ref <c>|*x|</c> forms, a non-constant range bound, or a body that
    /// <c>break</c>s/<c>continue</c>s the (now-absent) loop are clear deferred errors.</summary>
    private CStmt LowerInlineLoop(Item loopItem)
    {
        switch (loopItem.Content)
        {
            // `inline for (lo..hi) |i|` — the counted range. Bounds fold NOW (during this pass) via the
            // const-eval interpreter — literals / constant arithmetic / sizeof — so a forward-referenced
            // comptime CALL in a bound (whose callee may not be lowered yet) is intentionally not folded.
            case Zig.StmtForRange f:
            {
                if (_ir.ConstEval(LowerExpr(f.Arg2)) is not { } lo || _ir.ConstEval(LowerExpr(f.Arg4)) is not { } hi)
                {
                    throw new IrUnsupportedException(
                        "`inline for` bounds must be compile-time-known integer constants");
                }
                if (hi < lo)
                {
                    throw new IrUnsupportedException(
                        $"`inline for` upper bound ({hi}) is below the lower bound ({lo})");
                }
                // The capture is the usize index, bound to a literal in each copy.
                return UnrollInlineFor(hi - lo, Tok(f.Arg7), CType.ULong, f.Arg9,
                    k => new LitInt((lo + k).ToString(System.Globalization.CultureInfo.InvariantCulture), lo + k) { Type = CType.ULong });
            }

            // `inline for (<comptime list>) |x|` — over a `@typeInfo` member list or a `[_]type{…}`
            // literal (road-to-zig-std S6): the consumer S5c's lists exist for. A comptime list has
            // no runtime representation, so these three cases MUST precede the runtime-array case
            // below, and the capture binds comptime (UnrollComptimeFor), not as an emitted `const`.
            case Zig.StmtForSlice cf when TryComptimeIterable(cf.Arg2, out var cl):
                return UnrollComptimeFor(new[] { (cl, Tok(cf.Arg5)) }, cf.Arg7);

            // `inline for (a, b) |x, y|` — two lists walked in lockstep. Measured in the pinned std
            // this is the DOMINANT member-list shape (`(field_names, field_types)`, 17 uses). Both
            // operands must be comptime lists: a comptime list paired with a runtime slice cannot be
            // unrolled at all, so naming that beats a downstream type error.
            case Zig.StmtForSlicePair cp when TryComptimeIterable(cp.Arg2, out var cl0):
            {
                if (!TryComptimeIterable(cp.Arg4, out var cl1))
                {
                    throw new IrUnsupportedException(
                        "`inline for` over parallel operands requires BOTH to be comptime lists "
                        + $"(`{cl0.Label}` is one; the second operand is not)");
                }
                return UnrollComptimeFor(new[] { (cl0, Tok(cp.Arg7)), (cl1, Tok(cp.Arg9)) }, cp.Arg11);
            }

            // `inline for (list, 0..) |x, i|` — the list alongside its own indices, which is just a
            // second index-parallel operand (IndexList). The index must start at 0, as everywhere
            // else dotcc accepts `for (s, N..)`.
            case Zig.StmtForSliceIdx ci when TryComptimeIterable(ci.Arg2, out var cli):
            {
                if (_ir.ConstEval(LowerExpr(ci.Arg4)) is not 0)
                {
                    throw new IrUnsupportedException(
                        "`inline for` over a comptime list with an index capture must start the index at 0 "
                        + "(`for (list, 0..) |x, i|`)");
                }
                return UnrollComptimeFor(
                    new[] { (cli, Tok(ci.Arg8)), (IndexList(cli.Count), Tok(ci.Arg10)) }, ci.Arg12);
            }

            // `inline for (cs) |c|` over a comptime STRING (std.fmt.parse_float's FloatStream.firstIsLower, a
            // `comptime cs: []const u8` seed): one copy per byte, the capture bound to that byte as a literal.
            case Zig.StmtForSlice ss when EvalComptimeValue(ss.Arg2) is LitStr csv:
            {
                var bytes = DotCC.EmitHelpers.StringByteValues(csv.Segments).ToList();
                return UnrollInlineFor(bytes.Count, Tok(ss.Arg5), CType.UChar, ss.Arg7,
                    k => new LitInt(((int)bytes[(int)k]).ToString(System.Globalization.CultureInfo.InvariantCulture), bytes[(int)k]) { Type = CType.Int });   // an int constant narrows to the u8 capture
            }

            // `inline for (arr) |x|` — over a fixed array of comptime-known length. The operand must be
            // a named array variable (so each element read `arr[k]` is side-effect-free across copies);
            // the capture binds to the element by value.
            case Zig.StmtForSlice fs:
            {
                var operand = LowerExpr(fs.Arg2);
                if (operand.Type.Unqualified is not CType.Array arr || arr.Count is not int n)
                {
                    throw new IrUnsupportedException(
                        "`inline for` over a value requires a fixed-size array `[N]T` of comptime-known "
                        + "length (a slice's length is a runtime value)");
                }
                // A comptime-known array VALUE (std.crypto.md5's `const round0 = comptime [_]RoundParam{ roundParam(…), … };`):
                // each copy binds its element as a literal, so `v[r.a]` indexes by a constant, as zig's unrolled copy does.
                if (operand is not VarRef && _ir.EvalComptimeValue(operand) is IrModule.CtArray ctArray && ctArray.Elems.Length == n)
                {
                    var spliced = new List<CExpr>(n);
                    foreach (var elem in ctArray.Elems)
                    {
                        spliced.Add(_ir.SpliceComptimeValue(elem)
                            ?? throw new IrUnsupportedException("`inline for` over a comptime array: an element has no static form"));
                    }
                    return UnrollInlineFor(n, Tok(fs.Arg5), arr.Element, fs.Arg7, k => spliced[(int)k]);
                }
                if (operand is not VarRef)
                {
                    throw new IrUnsupportedException(
                        "`inline for` over an array requires a named array variable in V1 (so each "
                        + "element read is side-effect-free under unrolling)");
                }
                // Re-lower the operand per copy (a VarRef is idempotent) so each `arr[k]` is its own node.
                return UnrollInlineFor(n, Tok(fs.Arg5), arr.Element, fs.Arg7,
                    k => new DotCC.Ir.Index(LowerExpr(fs.Arg2), new LitInt(k.ToString(System.Globalization.CultureInfo.InvariantCulture), k) { Type = CType.ULong }) { Type = arr.Element });
            }

            // `inline while (cond) : (i = i + step) body` — comptime-UNROLLED while (Milestone T,
            // part 3). The loop counter must be a `comptime var` mutated by the continue-expression;
            // each round folds the condition (with the counter substituted in), unrolls a body copy,
            // then applies the continue-expr to advance the counter — all at lowering time.
            case Zig.StmtWhileContAssign w:
                return UnrollInlineWhile(w.Arg2, w.Arg6, w.Arg7, w.Arg8, w.Arg10);
            // `inline while (true) { … }` with no continue-expression (std.Io.Writer.print's outer loop): it
            // unrolls until a comptime `break`.
            case Zig.StmtWhile w:
                return UnrollInlineWhile(w.Arg2, null, null, null, w.Arg4);

            default:
                throw new IrUnsupportedException(
                    "`inline` is only supported on a counted `for (lo..hi) |i|` range loop, a "
                    + "`for (arr) |x|` over a fixed array, a `for` over a comptime member list "
                    + "(single, parallel `(a, b) |x, y|`, or indexed `(list, 0..) |x, i|`), or an "
                    + "`inline while (c) : (i = …)` with a `comptime var` counter (comptime "
                    + "unrolling) — the by-ref `|*x|` `for` forms, `inline for` over a runtime "
                    + "slice, and a bare/expr-cont `inline while` are not supported yet");
        }
    }

    /// <summary>Unroll an <c>inline while (cond) : (lhs = rhs) body</c> (Milestone T, part 3). The
    /// continue-expression's target must be a <c>comptime var</c> counter (so its value is known at
    /// lowering time). Each round: fold <paramref name="condItem"/> (with the counter's current value
    /// substituted) — stop when false; lower a body copy; fold the continue-expression's RHS and store
    /// it back as the counter's new value. The loop vanishes (no runtime <c>while</c>); a bare
    /// <c>break</c>/<c>continue</c> in the body, a non-comptime-var counter, or a non-foldable
    /// condition / continue value are clear errors. The unroll count is capped (a non-terminating
    /// comptime condition otherwise loops forever).</summary>
    private CStmt UnrollInlineWhile(Item condItem, Item? contLhsItem, Item? contOpItem, Item? contRhsItem, Item bodyItem)
    {
        // The continue-expr target must resolve (WITHOUT substitution) to a tracked comptime var.
        Symbol? contSym = null;
        if (contLhsItem is not null)
        {
            if (contLhsItem.Content is not Zig.Ident contId
                || _symbols.Resolve(Tok(contId.Arg0)) is not { } cs
                || !_comptimeVars.ContainsKey(cs))
            {
                throw new IrUnsupportedException(
                    "`inline while` requires a `comptime var` loop counter advanced by the "
                    + "continue-expression (`comptime var i = …; inline while (i < N) : (i += step) { … }`)");
            }
            contSym = cs;
        }

        // A comptime `break` / `continue` in the body is comptime control (road-to-zig-std G3): a copy that
        // ends in `break` is the last one (the continue-expression does not run), one that ends in
        // `continue` just goes on. Under a `switch` a `break` lowers to a goto this target names.
        var target = new LoopBreakTarget { BreakLabel = "__inl" + _loopLabelCounter++ + "_brk" };
        var copies = new List<CStmt>();
        _inlineUnrollDepth++;
        _loopBreakTargets.Push(target);
        try
        {
            while (true)
            {
                if (TryFoldComptimeCondition(condItem) is not { } holds)
                {
                    holds = _ir.ConstEval(LowerExpr(condItem)) is { } cond
                        ? cond != 0
                        : throw new IrUnsupportedException("`inline while` condition must be compile-time-known");
                }
                if (!holds) { break; }
                if (copies.Count >= InlineUnrollCap)
                {
                    throw new IrUnsupportedException(
                        $"`inline while` exceeded the unroll cap ({InlineUnrollCap}) — a non-terminating comptime condition?");
                }
                // Unroll one body copy (the comptime counter substitutes to its current value within it).
                _symbols.EnterScope();
                var body = LowerStmt(bodyItem);
                _symbols.ExitScope();
                var (trimmed, jump) = TrimTrailingJump(body, target.BreakLabel);
                if (HasLoopEscape(trimmed) || ContainsGotoTo(trimmed, target.BreakLabel))
                {
                    throw new IrUnsupportedException(
                        "a `break`/`continue` inside an `inline while` body that is not comptime control flow (the loop "
                        + "is unrolled, so a runtime-conditional one has no loop to leave) is not supported yet");
                }
                copies.Add(trimmed is Block ? trimmed : new Block(new List<CStmt> { trimmed }));
                if (jump is Break) { break; }
                if (contSym is null || contLhsItem is not { } lhsItem || contRhsItem is not { } rhsItem) { continue; }
                // Advance the counter: fold the continue-expr (with the current value), store it back.
                CExpr step = LowerExpr(rhsItem);
                if (contOpItem is not null && CompoundOpOf(contOpItem) is { } op)
                {
                    step = new Binary(op, LowerExpr(lhsItem), step) { Type = CType.Long };
                }
                if (_ir.ConstEval(step) is not { } next)
                {
                    throw new IrUnsupportedException("`inline while` continue-expression must be compile-time-known");
                }
                _comptimeVars[contSym] = (next, _comptimeVars[contSym].Type);
            }
        }
        finally
        {
            _loopBreakTargets.Pop();
            _inlineUnrollDepth--;
        }
        return new Seq(copies);
    }

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
            if (_currentFnSym is { } owner && !_zigInlineFns.Contains(owner)) { _comptimeReturnFns.Add(owner); }
            return TryComptimeReturnBlock(stmts, ret) ?? LowerBlock(blockItem);
        }
        _symbols.EnterScope();
        _comptimeDepth++;   // a comptime block's calls run at compile time (task #92)
        try { ExecuteComptimeStmt(blockItem); }
        finally { _comptimeDepth--; }
        _symbols.ExitScope();
        return new Seq(new List<CStmt>());   // compile-time-only — nothing runs at runtime
    }

    /// <summary>A <c>comptime { …; return &amp;final; }</c> block in a SLICE-returning function (std.enums.valuesFromFields):
    /// zig's slice points into comptime memory, but lowered as runtime code it would point into the frame's
    /// <c>stackalloc</c> and dangle once the function returns (a silent miscompile). So the block is lowered into a
    /// throwaway scope, its return value captured, and the whole run by the comptime interpreter; the evaluated slice
    /// becomes a pinned static. Null when the block does not evaluate at compile time (the caller lowers it plainly).</summary>
    private CStmt? TryComptimeReturnBlock(IReadOnlyList<Item> stmts, Zig.StmtReturn ret)
    {
        if (_currentFnRet?.Unqualified is not CType.Slice retSlice) { return null; }
        Symbol result;
        CStmt body;
        _symbols.EnterScope();
        try
        {
            using var hoist = EnterFreshHoist();
            var lowered = new List<CStmt>();
            for (var i = 0; i < stmts.Count - 1; i++) { lowered.Add(LowerStmt(stmts[i])); }
            var value = LowerExprSink(ret.Arg1, retSlice);
            lowered.AddRange(_hoist ?? new List<CStmt>());
            result = _symbols.Declare(new Symbol { Name = "__ctret", Kind = SymKind.Var, Type = retSlice });
            lowered.Add(new ExprStmt(new Assign(null, new VarRef(result) { Type = retSlice, IsLValue = true }, value) { Type = retSlice }));
            body = new Block(lowered);
        }
        catch (IrUnsupportedException) { return null; }
        finally { _symbols.ExitScope(); }
        return _ir.EvalComptimeBlock(body, result) is IrModule.CtSlice slice && _ir.SpliceComptimeValue(slice) is { } spliced
            ? new Return(spliced)
            : null;
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
    private bool FoldComptimeBlockCondition(Item cond)
    {
        if (TryFoldComptimeCondition(cond) is { } folded) { return folded; }
        using (EnterThrowawayHoist())
        {
            if (_ir.ConstEval(LowerExpr(cond)) is { } v) { return v != 0; }
        }
        throw new IrUnsupportedException("zig: an `if` in a `comptime` block needs a compile-time-known condition");
    }

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
        if (v >= 0)
        {
            return new LitInt(v.ToString(System.Globalization.CultureInfo.InvariantCulture), v) { Type = t };
        }
        var mag = -(System.Int128)v;
        return new Unary(UnOp.Neg, new LitInt(mag.ToString(System.Globalization.CultureInfo.InvariantCulture), v == long.MinValue ? null : -v) { Type = t }) { Type = t };
    }

    /// <summary>Build the unrolled copies of an <c>inline for</c> body: for each of
    /// <paramref name="count"/> iterations, a block <c>{ const capture = initFor(k); body }</c> with the
    /// capture freshly declared in its own scope (sibling blocks may reuse the name in C#; the symbol
    /// table's CS0136 rename covers any leak regardless). A <c>break</c>/<c>continue</c> in the body is
    /// retargeted by <see cref="InlineUnroll"/>. The count is capped to bound emitted-code size.</summary>
    private CStmt UnrollInlineFor(long count, string captureName, CType captureType, Item bodyItem, System.Func<long, CExpr> initFor)
    {
        if (count > InlineUnrollCap)
        {
            throw new IrUnsupportedException(
                $"`inline for` would unroll {count} iterations, exceeding the cap ({InlineUnrollCap})");
        }
        var unroll = new InlineUnroll(_blockLabelCounter++);
        for (long k = 0; k < count; k++)
        {
            _symbols.EnterScope();
            // A comptime-known capture (a counted range's index) IS its value in every comptime question,
            // as zig has it: `const block_x_len = block_len / (1 << j); comptime if (block_x_len < 4) break;`
            // in std.mem.findScalarPos folds through it.
            var init = initFor(k);
            var folded = captureType.Unqualified is CType.Prim { Integer: true } ? _ir.ConstEval(init) : null;
            var sym = _symbols.Declare(new Symbol
            {
                Name = captureName, Kind = SymKind.Var, Type = captureType,
                IsConstexpr = folded is not null, ConstValue = folded ?? 0,
            });
            // A copy is analysed with its index comptime-known, so its constant conditions settle as zig's do.
            _inlineUnrollDepth++;
            CStmt body;
            try { body = LowerStmt(bodyItem); }
            finally { _inlineUnrollDepth--; }
            _symbols.ExitScope();
            // `inline for (0..2) |_|` discards the index: no declaration (an unused `_` local is CS0219).
            var copy = captureName == "_" ? new List<CStmt> { body }
                : new List<CStmt> { new DeclStmt(new List<LocalDecl> { new(sym, init) }), body };
            if (!unroll.Add(new Block(copy))) { break; }
        }
        return unroll.Finish();
    }

    /// <summary>Does this statement contain a bare <c>break</c>/<c>continue</c> that would target an
    /// enclosing loop (as opposed to one nested inside it)? Used to reject loop-control inside an
    /// <c>inline for</c> body, where unrolling removes the loop. Descends into blocks / sequences /
    /// <c>if</c> branches / labeled statements, but NOT into a nested loop or switch — a
    /// break/continue there binds to that construct, not to the unrolled <c>inline for</c>.</summary>
    private static bool HasLoopEscape(CStmt s) => s switch
    {
        Break or Continue => true,
        Block b           => b.Stmts.Any(HasLoopEscape),
        Seq q             => q.Stmts.Any(HasLoopEscape),
        If i              => HasLoopEscape(i.Then) || (i.Else is { } e && HasLoopEscape(e)),
        Labeled l         => HasLoopEscape(l.Body),
        _                 => false,
    };

    /// <summary>Rebuild a loop statement with its body transformed by <paramref name="f"/> — used to
    /// append a labeled loop's continue label to the end of the body. Defensive: anything that isn't a
    /// loop is returned untouched (the grammar's <c>LoopStmt</c> guarantees a loop here).</summary>
    private static CStmt WithLoopBody(CStmt loop, Func<CStmt, CStmt> f) => loop switch
    {
        While w  => new While(w.Cond, f(w.Body)),
        For fr   => new For(fr.Init, fr.Cond, fr.Post, f(fr.Body)),
        DoWhile d => new DoWhile(f(d.Body), d.Cond),
        _ => loop,
    };

    /// <summary>Append a (no-op-bodied) label to a statement, flattening into an existing block so the
    /// label sits at the body's end. The label wraps an empty <see cref="Block"/> (<c>lbl: { }</c>)
    /// because a C# label can't directly precede a declaration (CS1023).</summary>
    private static CStmt AppendLabel(CStmt body, string label)
    {
        var labeled = new Labeled(label, new Block(new List<CStmt>()));
        var stmts = body is Block b ? new List<CStmt>(b.Stmts) : new List<CStmt> { body };
        stmts.Add(labeled);
        return new Block(stmts);
    }

    /// <summary>Lower <c>break :lbl;</c> / <c>continue :lbl;</c> (Milestone L, part 3) to a <c>goto</c>
    /// to the enclosing labeled loop's break / continue label (resolved innermost-first, marking the
    /// label used so it gets emitted). A label that names a value-block (not a loop) is a clear error
    /// — a loop <c>break</c>/<c>continue</c> can't target a value-block.</summary>
    private CStmt LowerLabeledLoopJump(string label, bool isContinue)
    {
        var t = _labeledLoops.FirstOrDefault(l => l.Label == label);
        if (t is null)
        {
            var what = isContinue ? "continue" : "break";
            if (_labeledBlocks.Any(b => b.Label == label))
            {
                throw new IrUnsupportedException(
                    $"`{what} :{label}` targets a labeled block, but a labeled block isn't a loop (use `break :{label} <value>;` to yield its value)");
            }
            throw new IrUnsupportedException($"`{what} :{label}` has no enclosing labeled loop ':{label}'");
        }
        if (isContinue) { t.ContUsed = true; return new Goto(t.ContLabel); }
        t.BreakUsed = true;
        return new Goto(t.BreakLabel);
    }

    /// <summary>Lower a payload-capturing <c>if (cond) |x| then [else …]</c> (Milestone M). The
    /// branch test and the binding depend on the condition's lowered type:
    /// <list type="bullet">
    /// <item>a value optional <c>?T</c> (<see cref="CType.Optional"/>) → test <c>__cap.HasValue</c>,
    /// bind <c>x = __cap.Value</c> at the top of the then-branch;</item>
    /// <item>a niche optional pointer (lowered to a bare <c>T*</c>) → test the pointer for non-null
    /// (the <c>Cond.B(void*)</c> overload), bind <c>x = __cap</c> (the unwrapped pointer is the
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

    private CStmt LowerIfStmt(Item condItem, Item thenItem, Item? elseItem)
    {
        // `if (@inComptime()) { … } else { … }` (std.mem.swap): both arms stay. Emitted code takes the runtime one (the
        // condition renders `false`), and the comptime interpreter, which reads `@inComptime()` as true, the other.
        if (IsInComptimeTest(condItem))
        {
            return new If(LowerExpr(condItem), LowerStmt(thenItem), elseItem is { } inElse ? LowerStmt(inElse) : null);
        }
        // A COMPTIME TAG condition (road-to-zig-std S3a) — `builtin.cpu.arch == .x86_64`,
        // `builtin.os.tag != .windows`. Folded before the operand is lowered at all, because the
        // untaken arm is exactly the platform code that must not be lowered: the inline asm, the
        // syscall wrappers, the per-arch intrinsics the branch exists to avoid. See LowerBinary's
        // own tag fold, which is where the comparison would otherwise settle to a runtime bool.
        if (TryFoldComptimeCondition(condItem) is { } tagCond)
        {
            if (tagCond) { return LowerStmt(thenItem); }
            return elseItem is { } tagTaken ? LowerStmt(tagTaken) : new Seq(new List<CStmt>());
        }
        // In an unrolled `inline for` or a generic instance, `c and rest` with a constant-false `c` (or `c or rest` with
        // a constant-true one) is settled whatever `rest` is, and zig analyses no dead branch: std.mem.eqlBytes' unrolled
        // `if (n <= Scan.size and a.len <= n) { const V = @Vector(n / 2, u8); … }` must not form `@Vector(128, u8)`.
        if ((_inlineUnrollDepth > 0 || _inGenericInstance) && TrySettleByLeftOperand(condItem) is { } settled)
        {
            if (settled) { return LowerStmt(thenItem); }
            return elseItem is { } settledElse ? LowerStmt(settledElse) : new Seq(new List<CStmt>());
        }
        var cond = LowerExpr(condItem);
        if (_inGenericInstance && _ir.ConstEval(cond) is { } cv)
        {
            if (cv != 0) { return LowerStmt(thenItem); }
            return elseItem is { } taken ? LowerStmt(taken) : new Seq(new List<CStmt>());
        }
        return new If(cond, LowerStmt(thenItem), elseItem is { } el ? LowerStmt(el) : null);
    }

    /// <summary>True for <c>@inComptime()</c> or <c>!@inComptime()</c>, parenthesized or not.</summary>
    private static bool IsInComptimeTest(Item cond)
    {
        while (cond.Content is Zig.Grouped g) { cond = g.Arg1; }
        if (cond.Content is Zig.PreNot n) { cond = n.Arg1; }
        while (cond.Content is Zig.Grouped g2) { cond = g2.Arg1; }
        return cond.Content is Zig.BuiltinCallNoArgs { Arg0: var tok } && Tok(tok) == "@inComptime";
    }

    /// <summary>An <c>and</c> whose left operand is a constant false, or an <c>or</c> whose left operand is a constant true:
    /// settled by that operand alone (the right one need not be comptime). Null otherwise.</summary>
    private bool? TrySettleByLeftOperand(Item condItem)
    {
        var cur = condItem;
        while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
        var (left, isAnd) = cur.Content switch
        {
            Zig.BoolAnd a => (a.Arg0, true),
            Zig.BoolOr o => (o.Arg0, false),
            _ => ((Item?)null, false),
        };
        if (left is null) { return null; }
        long? value;
        using (EnterThrowawayHoist())
        {
            try { value = _ir.ConstEval(LowerExpr(left)); }
            catch (IrUnsupportedException) { return null; }
        }
        return value switch
        {
            0 when isAnd => false,
            not null and not 0 when !isAnd => true,
            _ => null,
        };
    }

    /// <summary>Settle an <c>if</c> condition at lowering time when it is a COMPTIME question — a tag
    /// comparison (<c>builtin.os.tag == .windows</c>) or a module-exported boolean constant
    /// (<c>builtin.link_libc</c>). Null when it is not one, so the ordinary two-armed lowering runs.
    ///
    /// <para>Deliberately narrower than "any condition <see cref="IrModule.ConstEval"/> settles":
    /// that wider rule is what zig itself does, but it would change the emitted shape of every
    /// existing <c>if</c> over a constant, and nothing measured needs it. These two forms are what a
    /// platform conditional is made of, and they had no runtime meaning to lose.</para></summary>
    private bool? TryFoldComptimeCondition(Item condItem)
    {
        var cur = condItem;
        while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
        if (cur.Content is Zig.CmpEq eq && TryFoldComptimeTagCompare(eq.Arg0, eq.Arg2, negate: false, out var eqVal))
        {
            return eqVal is LitBool { Value: var e } && e;
        }
        if (cur.Content is Zig.CmpNe ne && TryFoldComptimeTagCompare(ne.Arg0, ne.Arg2, negate: true, out var neVal))
        {
            return neVal is LitBool { Value: var n } && n;
        }
        // `T == u8` / `T != Elem` — a TYPE comparison (the W4 lift; `std.meta`/`std.math` type bodies
        // ask it). Only when BOTH operands are types — a type has no runtime value, so there is no runtime
        // comparison this could be stealing.
        if (cur.Content is Zig.CmpEq teq && TryFoldTypeEquality(teq.Arg0, teq.Arg2) is { } typesEqual)
        {
            return typesEqual;
        }
        if (cur.Content is Zig.CmpNe tne && TryFoldTypeEquality(tne.Arg0, tne.Arg2) is { } typesDiffer)
        {
            return !typesDiffer;
        }
        // `if (builtin.link_libc)` / `if (!builtin.single_threaded)` — a module-exported bool.
        if (cur.Content is Zig.PreNot not)
        {
            return TryFoldComptimeCondition(not.Arg1) is { } inner ? !inner : null;
        }
        // `and` / `or` over two comptime questions — `builtin.os.tag == .linux and builtin.link_libc`. A settled
        // LEFT side that decides the result short-circuits, exactly as zig does (`T == bool and cpu.has(…)` in
        // std.simd never analyses the right side when `T` is not `bool`): the right side is not evaluated at all,
        // at comptime or at runtime. Otherwise both sides must settle, since a runtime half must still run.
        if (cur.Content is Zig.BoolAnd conj)
        {
            var la = TryFoldComptimeCondition(conj.Arg0);
            if (la == false) { return false; }
            return la == true ? TryFoldComptimeCondition(conj.Arg2) : null;
        }
        if (cur.Content is Zig.BoolAndSwitch conjSw)
        {
            return TryFoldComptimeCondition(conjSw.Arg0) is { } lsw && TryFoldComptimeCondition(conjSw.Arg2) is { } rsw
                ? lsw && rsw
                : null;
        }
        if (cur.Content is Zig.BoolOrSwitch disjSw)
        {
            return TryFoldComptimeCondition(disjSw.Arg0) is { } losw && TryFoldComptimeCondition(disjSw.Arg2) is { } rosw
                ? losw || rosw
                : null;
        }
        if (cur.Content is Zig.BoolOr disj)
        {
            var lo = TryFoldComptimeCondition(disj.Arg0);
            if (lo == true) { return true; }
            return lo == false ? TryFoldComptimeCondition(disj.Arg2) : null;
        }
        // `@hasDecl(root, "std_options")` / `@hasField(T, "x")`: a membership question, always comptime.
        if (cur.Content is Zig.BuiltinCall { Arg0: var memberTok } memberCall && Tok(memberTok) is "@hasDecl" or "@hasField")
        {
            try { return TryEvalMembershipBuiltin(memberCall)?.Value; }
            catch (IrUnsupportedException) { return null; }
        }
        // A comptime bool bound earlier in the body (`const is_comptime = @TypeOf(x) == comptime_int;`).
        if (cur.Content is Zig.Ident bid && _symbols.Resolve(Tok(bid.Arg0)) is null
            && _comptimeValues.TryGetValue(Tok(bid.Arg0), out var boundBool) && boundBool is LitBool { Value: var bb })
        {
            return bb;
        }
        // A module-level comptime bool (`if (runtime_safety)` in debug.zig), folded from its declaration, so
        // the question has an answer while containers are still registering, before any global exists, and
        // stays foldable once it is one (zig forbids a local shadowing a declaration, so the name is it).
        if (cur.Content is Zig.Ident tid && Tok(tid.Arg0) is var topName
            && _symbols.Resolve(topName) is null or { IsGlobal: true }
            && _topLevelConstRhs.TryGetValue(topName, out var topRhs) && _foldingTopLevelConsts.Add(topName))
        {
            try { return TryFoldComptimeCondition(topRhs); }
            finally { _foldingTopLevelConsts.Remove(topName); }
        }
        // `switch (builtin.mode) { .Debug, .ReleaseSafe => true, … }`: a switch over a comptime tag folds to
        // the value of the prong it selects.
        if (cur.Content is Zig.SwitchExpr or Zig.SwitchExprTrailing)
        {
            var (subject, prongs) = cur.Content switch
            {
                Zig.SwitchExpr s => (s.Arg2, s.Arg5),
                Zig.SwitchExprTrailing s => (s.Arg2, s.Arg5),
                _ => throw new System.InvalidOperationException(),
            };
            if (SelectComptimeProng(subject, prongs, out var chosenPayload) is not { Expr: { } chosen } chosenProng) { return null; }
            if (chosenProng.CaptureName is null) { return TryFoldComptimeCondition(chosen); }
            // `.int => |info| @sizeOf(T) * 8 == info.bits` (std.meta.hasUniqueRepresentation): the capture
            // binds the tag's payload for the prong's question.
            EnterComptimeProng(chosenProng, chosenPayload);
            try { return TryFoldComptimeCondition(chosen); }
            finally { ExitComptimeProng(); }
        }
        // `if (comptime typeContainsSlice(Key)) @compileError(…)` (std.hash.autoHash): a comptime call to a
        // generic whose parameters are all comptime TYPES and whose body is `return <question>;` folds by
        // asking the question with the arguments bound, so the guarded `@compileError` is never analysed.
        if (cur.Content is Zig.PreComptime { Arg1: var comptimeCall } && TryFoldComptimeBoolCall(comptimeCall) is { } called)
        {
            return called;
        }
        // The same question asked without `comptime` (`if (std.meta.hasUniqueRepresentation(Key))` in autoHash):
        // a function of TYPES only, with a single `return`, is pure, so its answer is the same at comptime.
        if (cur.Content is Zig.CallArgs && TryFoldComptimeBoolCall(cur) is { } plainCalled)
        {
            return plainCalled;
        }
        // A comparison inside the body of a comptime TYPE question (`@sizeOf(T) * 8 == info.bits` in
        // hasUniqueRepresentation): there every operand is comptime by construction (the function takes only
        // types), so the interpreter's answer is the question's. NOT folded elsewhere: ConstEval reads a local
        // `const` through its initializer with plain integer arithmetic, which is not a wrapping `u8`'s.
        if (_comptimeBoolCallDepth > 0 && cur.Content is Zig.CmpEq or Zig.CmpNe or Zig.CmpLt or Zig.CmpGt or Zig.CmpLe or Zig.CmpGe
            && TryConstEvalCondition(cur) is { } compared)
        {
            return compared;
        }
        if (cur.Content is Zig.TrueLit) { return true; }
        if (cur.Content is Zig.FalseLit) { return false; }
        // A question about a comptime AGGREGATE (`cpu.has(.x86, .avx2)` over a `comptime cpu: std.Target.Cpu`
        // parameter, `cpu.arch.isX86()`): every operand is comptime, so the interpreter's answer is the question's.
        if (IsRootedAtComptimeAggregate(cur) && TryInterpretCondition(cur) is { } aggregateAnswer)
        {
            return aggregateAnswer;
        }
        return TryFoldImportedComptimeValue(cur, out var v) && v is LitBool { Value: var b } ? b : null;
    }

    /// <summary>Compare two TYPE operands at comptime, or null when either is not a type (so the caller
    /// keeps looking). Types compare by their resolved <see cref="CType"/> AND their declared integer
    /// width: dotcc widens <c>u21</c> and <c>u32</c> to the same C# <c>uint</c>, but they are different
    /// types in zig, and <c>T == u32</c> must say so.</summary>
    /// <summary>True when an expression is a member access or method call whose innermost base names a comptime
    /// aggregate the interpreter holds (<see cref="IrModule.ComptimeGlobals"/>): <c>cpu.has(…)</c>, <c>cpu.arch</c>.</summary>
    private bool IsRootedAtComptimeAggregate(Item expr)
    {
        var cur = expr;
        while (true)
        {
            switch (cur.Content)
            {
                case Zig.Grouped g: cur = g.Arg1; continue;
                case Zig.Field f: cur = f.Arg0; continue;
                case Zig.CallArgs ca: cur = ca.Arg0; continue;
                case Zig.CallNoArgs cn: cur = cn.Arg0; continue;
                case Zig.Ident id:
                    return _symbols.Resolve(Tok(id.Arg0)) is { } sym && _ir.ComptimeGlobals.ContainsKey(sym);
                default:
                    return false;
            }
        }
    }

    /// <summary>Lower a condition (under a throwaway hoist) and ask the interpreter for its boolean value; null when
    /// it does not evaluate.</summary>
    private bool? TryInterpretCondition(Item cond)
    {
        CExpr lowered;
        using (EnterThrowawayHoist()) { lowered = LowerExpr(cond); }
        return _ir.ResolveComptimeFold(lowered) switch
        {
            LitBool b => b.Value,
            LitInt i when i.Value is { } n => n != 0,
            _ => null,
        };
    }

    private bool? TryFoldTypeEquality(Item left, Item right)
    {
        // `T == comptime_int` (std.math.Log2Int's first line). A `comptime T: type` bound to a concrete type is
        // never a comptime number; `@TypeOf(x)` of a comptime_int `anytype` is (CType.ComptimeInt, target T5).
        if (IsComptimeNumberTypeName(right) && TryTypeAliasRhs(left, out var leftType))
        {
            return Tok(((Zig.Ident)right.Content).Arg0) == "comptime_int" && leftType.Unqualified is CType.Prim { IsComptimeInt: true };
        }
        if (IsComptimeNumberTypeName(left) && TryTypeAliasRhs(right, out var rightType))
        {
            return Tok(((Zig.Ident)left.Content).Arg0) == "comptime_int" && rightType.Unqualified is CType.Prim { IsComptimeInt: true };
        }
        // A zig float dotcc does not lower (`T == f16 or T == f32 or T == f64` in std.fmt.parseFloat) is never equal to
        // a type that does.
        if (IsUnmodeledPrimitiveType(right) && TryTypeAliasRhs(left, out _)) { return false; }
        if (IsUnmodeledPrimitiveType(left) && TryTypeAliasRhs(right, out _)) { return false; }
        if (!TryTypeAliasRhs(left, out var lt)) { return null; }
        var lb = DeclaredBitsOfTypeArg(left);
        if (!TryTypeAliasRhs(right, out var rt)) { return null; }
        var rb = DeclaredBitsOfTypeArg(right);
        // A side with no recorded width is its carrier's (an alias whose width was never tracked); comparing the raw null
        // against `u64`'s 64 had made `DT == u64` false for `const DT = if (…) u64 else u128;`, silently (task #77).
        static int? Carrier(CType t) => t.Unqualified is CType.Prim { Integer: true, Bytes: var bytes } ? bytes * 8 : null;
        return lt.Unqualified.Equals(rt.Unqualified) && (lb ?? Carrier(lt)) == (rb ?? Carrier(rt));
    }

    /// <summary>True for the bare names <c>comptime_int</c> / <c>comptime_float</c> — zig's untyped
    /// compile-time number types, which dotcc never binds a type parameter to.</summary>
    private static bool IsComptimeNumberTypeName(Item item)
        => item.Content is Zig.Ident id && Tok(id.Arg0) is "comptime_int" or "comptime_float";

    /// <summary>True for a zig primitive type name dotcc does not lower (<c>f16</c>, <c>f80</c>, <c>f128</c>,
    /// <c>c_longdouble</c>, the zero-width <c>u0</c> / <c>i0</c>): a comparison against one still answers, since no
    /// lowered type is it (std.bit_set's <c>if (MaskInt == u0) return;</c>).</summary>
    private static bool IsUnmodeledPrimitiveType(Item item)
        => item.Content is Zig.Ident id && Tok(id.Arg0) is "f16" or "f80" or "f128" or "c_longdouble" or "u0" or "i0";

    /// <summary>Lower a captured <c>if</c> statement: <c>if (opt) |x|</c> over an optional, an optional pointer or an
    /// error union (with <c>else |e|</c>). <paramref name="byRef"/> is the by-ref form <c>if (opt) |*x|</c> (task #94):
    /// <c>x</c> points AT the payload in place (a value optional's through <c>ZigMem.OptionalPayload</c>, an optional
    /// pointer's own slot), so the condition is used as the lvalue it names rather than copied.</summary>
    private CStmt LowerIfCapture(Item condItem, string capName, Item thenItem, Item? elseItem, string? errCapName,
        bool byRef = false)
    {
        // Comptime fold (S4b): a captured `if` on a comptime-known optional (a `comptime x: ?T` seed)
        // selects the taken branch at lowering time. `null` → the else (empty if absent); a known payload
        // → the then with `x` bound to the literal. Only for the plain optional form (no `else |e|`).
        if (errCapName is null && TryComptimeOptionalCond(condItem, out var copt))
        {
            if (byRef)
            {
                throw new IrUnsupportedException(
                    "zig `if (opt) |*x|` over a comptime-known optional: a comptime value has no runtime payload to point at");
            }
            if (!copt.HasValue) { return elseItem is { } el ? LowerStmt(el) : new Seq(new List<CStmt>()); }
            _symbols.EnterScope();
            BindFoldedCapture(capName, copt.Value, copt.Inner);
            // The payload's declared width rides the capture (`if (comptime std.math.cast(usize, v)) |x|`: 64 bits),
            // so an `anytype` it is passed to can answer `@typeInfo(@TypeOf(x)).int.bits`.
            if (capName != "_" && _symbols.Resolve(capName) is { } foldedCap && DeclaredBitsOfArgument(condItem) is { } capBits)
            {
                RecordValueBits(foldedCap, capBits, null);
            }
            var folded = LowerStmt(thenItem);
            _symbols.ExitScope();
            return folded;
        }

        var cond = LowerExpr(condItem);
        var ct = cond.Type.Unqualified;

        // Hoist a side-effecting condition to a single-eval temp (a bare var is already re-readable).
        var pre = new List<CStmt>();
        CExpr condRef;
        // By ref, the condition IS the storage the capture points into (`@field(init_values, tag)`), so a repeatable
        // lvalue is used as is; anything else is a temporary, and the capture points into that.
        if (cond is VarRef || byRef && cond.IsLValue && IsRepeatableLValue(cond))
        {
            condRef = cond;
        }
        else
        {
            var tmp = _symbols.Declare(new Symbol { Name = "__cap", Kind = SymKind.Var, Type = cond.Type });
            pre.Add(new DeclStmt(new List<LocalDecl> { new(tmp, cond) }));
            condRef = new VarRef(tmp) { Type = cond.Type, IsLValue = true };
        }

        CExpr test;
        CExpr payloadInit;
        CType payloadType;
        if (ct is CType.Optional opt)
        {
            if (errCapName is not null)
            {
                throw new IrUnsupportedException(
                    "zig `if (optional) |x| … else |e|`: an optional has no error to capture (use a plain `else`)");
            }
            test = new Member(condRef, "HasValue", false) { Type = CType.Bool };
            payloadInit = new Member(condRef, "Value", false) { Type = opt.Inner };
            payloadType = opt.Inner;
            if (byRef)
            {
                // Into a `const` optional, the capture is a `*const T` (a store through it is rejected, task #95).
                payloadType = new CType.Pointer(IsConstStorage(condRef) ? opt.Inner.WithQuals(TypeQual.Const) : opt.Inner);
                payloadInit = new Call("ZigMem.OptionalPayload", new List<CExpr> { AddressOfLValue(condRef) },
                    new List<CType> { new CType.Pointer(cond.Type) }) { Type = payloadType };
            }
        }
        else if (ct is CType.Pointer)
        {
            if (errCapName is not null)
            {
                throw new IrUnsupportedException(
                    "zig `if (optional pointer) |x| … else |e|`: a pointer optional has no error to capture (use a plain `else`)");
            }
            test = condRef;        // Cond.B(void*) tests non-null
            payloadInit = condRef; // the unwrapped pointer is the same value
            payloadType = cond.Type;
            if (byRef)
            {
                // `|*p|` of an optional pointer points at the pointer variable itself (a `*const` one when the variable is).
                payloadInit = AddressOfLValue(condRef);
                if (IsConstStorage(condRef)) { payloadInit = payloadInit with { Type = new CType.Pointer(cond.Type.WithQuals(TypeQual.Const)) }; }
                payloadType = payloadInit.Type;
            }
        }
        else if (ct is CType.ErrorUnion && byRef)
        {
            throw new IrUnsupportedException("zig `if (error_union) |*x|`: a by-ref capture of an error union's payload is not modeled");
        }
        else if (ct is CType.ErrorUnion eu)
        {
            // Error union (Milestone M, part 3): bind the success payload to `x` in the then-branch,
            // the error to `e` (the runtime `ushort Code`) in the else-branch. We test `__cap.IsErr`
            // (a clean bool) and emit the ERROR branch as the C# `if`, success as `else` — so no `!`
            // is needed. NOTE: this is a value inspection (`.IsErr`), NOT `try`, so it never throws a
            // ZigErrorReturn — the error is handled HERE and does not propagate to the function's
            // boundary catch. The captured error binds as `CType.ErrorSet` (rendered `ushort`, the
            // flat global code), so `e == error.Foo` compares codes (Milestone N) — what un-erased
            // the part-3 cut: a USED named `|e|` is now valid in both compilers.
            var errStmts = new List<CStmt>();
            _symbols.EnterScope();
            if (errCapName is not null && errCapName != "_")
            {
                var errSym = _symbols.Declare(new Symbol { Name = errCapName, Kind = SymKind.Var, Type = CType.ErrorSet });
                errStmts.Add(new DeclStmt(new List<LocalDecl> { new(errSym, new Member(condRef, "Code", false) { Type = CType.ErrorSet }) }));
            }
            if (elseItem is not null) { errStmts.Add(LowerStmt(elseItem)); }
            _symbols.ExitScope();

            var okStmts = new List<CStmt>();
            _symbols.EnterScope();
            if (capName != "_")
            {
                var okSym = _symbols.Declare(new Symbol { Name = capName, Kind = SymKind.Var, Type = eu.Payload });
                okStmts.Add(new DeclStmt(new List<LocalDecl> { new(okSym, new Member(condRef, "Value", false) { Type = eu.Payload }) }));
            }
            okStmts.Add(LowerStmt(thenItem));
            _symbols.ExitScope();

            var errTest = new Member(condRef, "IsErr", false) { Type = CType.Bool };
            CStmt euIf = new If(errTest, new Block(errStmts), new Block(okStmts));
            if (pre.Count > 0) { pre.Add(euIf); return new Block(pre); }
            return euIf;
        }
        else
        {
            throw new IrUnsupportedException(
                "zig `if (...) |x|` requires an optional (or error-union) condition");
        }

        // then-branch: bind the payload at the top, with `x` in scope while lowering the branch.
        var thenStmts = new List<CStmt>();
        _symbols.EnterScope();
        if (capName != "_")
        {
            var capSym = _symbols.Declare(new Symbol { Name = capName, Kind = SymKind.Var, Type = payloadType });
            // The payload's declared width (`if (std.math.cast(isize, v)) |x|`: 64), for an `anytype` it reaches.
            if (DeclaredBitsOfLowered(cond) is { } runtimeCapBits) { RecordValueBits(capSym, runtimeCapBits, null); }
            thenStmts.Add(new DeclStmt(new List<LocalDecl> { new(capSym, payloadInit) }));
        }
        thenStmts.Add(LowerStmt(thenItem));
        _symbols.ExitScope();
        var thenBlock = new Block(thenStmts);

        var elseStmt = elseItem is null ? null : LowerStmt(elseItem);

        CStmt ifStmt = new If(test, thenBlock, elseStmt);
        if (pre.Count > 0)
        {
            pre.Add(ifStmt);
            return new Block(pre);
        }
        return ifStmt;
    }

    /// <summary><c>&amp;lvalue</c>, marking the variable at its root address-taken, as <c>&amp;x</c> in source does.</summary>
    private static CExpr AddressOfLValue(CExpr lvalue)
    {
        var root = lvalue;
        while (root is Member m) { root = m.Base; }
        if (root is VarRef { Sym: { Kind: SymKind.Var or SymKind.Param } rootSym }) { rootSym.AddressTaken = true; }
        return new Unary(UnOp.AddrOf, lvalue) { Type = new CType.Pointer(lvalue.Type) };
    }

    /// <summary>Lower a VALUE-position captured <c>if</c> — <c>if (opt) |x| thenE else elseE</c> (S4a),
    /// the expression sibling of <see cref="LowerIfCapture"/>. A pure ternary can't bind the payload
    /// <c>x</c>, so this hoists (ANF): a result temp is declared before the enclosing statement and
    /// assigned by a real <c>if</c> whose then-branch binds <c>x = opt.Value</c> (or the unwrapped
    /// pointer). <c>else</c> is mandatory (the grammar requires it — a value is yielded on both paths).
    /// The optional / optional-pointer forms mirror <see cref="LowerIfCapture"/>; an error-union value
    /// <c>if</c> is a separate (later) shape. Like the value <c>IfExpr</c> ternary, the branch
    /// expressions are lowered as values (a side-effecting sub-expression that itself hoists would be a
    /// shared latent limitation — the common case is pure branches / a comptime-known optional).</summary>
    /// <summary>True when a captured-<c>if</c> condition is a comptime-known OPTIONAL — a bare identifier
    /// (optionally parenthesized) resolving to a <c>comptime x: ?T</c> generic seed in
    /// <see cref="_comptimeOptionalVars"/>. Yields the seed (<c>HasValue</c> / payload <c>Value</c> /
    /// <c>Inner</c> type) so <see cref="LowerIfCapture"/> / <see cref="LowerIfCaptureExpr"/> can fold to
    /// the taken branch at lowering time (road-to-zig-std S4b).</summary>
    /// <summary>True when a call's callee is declared to return <c>?comptime_int</c> (so the call is comptime by its
    /// type): a plain function, a generic's template, or a function of another module, by its return-type AST.</summary>
    private bool ReturnsOptionalComptimeInt(Item call)
    {
        // Answered from the callee's DECLARED return type where it is a generic template (the call itself may
        // already have folded to its payload literal).
        var calleeAst = call.Content switch
        {
            Zig.CallArgs ca => ca.Arg0,
            Zig.CallNoArgs cn => cn.Arg0,
            _ => null,
        };
        if (calleeAst is not null && DeclaredReturnIsOptionalComptimeInt(calleeAst)) { return true; }
        var callee = call.Content switch
        {
            Zig.CallArgs ca => ca.Arg0,
            Zig.CallNoArgs cn => cn.Arg0,
            _ => null,
        };
        if (callee is null) { return false; }
        CExpr lowered;
        using (EnterThrowawayHoist()) { lowered = LowerExpr(call); }
        // The lowered return type of a `?comptime_int` is `Int128?` (comptime_int is the interpreter's 128 bits).
        return lowered.Type?.Unqualified is CType.Optional { Inner: var inner } && inner.Unqualified == CType.Int128;
    }

    /// <summary>True when a callee names a generic whose declared return type is <c>?comptime_int</c>, found through a
    /// module path (<c>std.simd.suggestVectorLength</c>) or by bare name.</summary>
    private bool DeclaredReturnIsOptionalComptimeInt(Item callee)
    {
        (ZigLowering Owner, Symbol Sym)? decl = callee.Content switch
        {
            Zig.Field f when ResolveModulePath(f.Arg0)?.Lowering is { } module => module.ResolveExportedDecl(Tok(f.Arg2)),
            Zig.Ident id => _symbols.Resolve(Tok(id.Arg0)) is { } s ? (this, s) : null,
            _ => null,
        };
        return decl is { } d && d.Owner._genericFns.TryGetValue(d.Sym, out var g)
            && g.RetType.Content is Zig.TyOptional { Arg1: var inner } && IsComptimeIntType(inner);
    }

    private bool TryComptimeOptionalCond(Item condItem, out (bool HasValue, long Value, CType Inner) info)
    {
        info = default;
        var cur = condItem;
        while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
        // `if (comptime f()) |x|`: a comptime OPTIONAL value (the comptime engine's E2 runs the call now,
        // lowering its body on demand), so `x` is a comptime integer and the branch folds.
        // A call returning `?comptime_int` (`if (std.simd.suggestVectorLength(T)) |block_len|` in std.mem) is
        // comptime by its type, exactly as if it were spelled `comptime`.
        var comptimeByType = cur.Content is Zig.CallArgs or Zig.CallNoArgs && ReturnsOptionalComptimeInt(cur);
        if (cur.Content is Zig.PreComptime || comptimeByType)
        {
            CExpr inner;
            using (EnterThrowawayHoist()) { inner = LowerExpr(cur.Content is Zig.PreComptime pc ? pc.Arg1 : cur); }
            // A comptime-only `?comptime_int` call may already be its folded value: the payload literal itself.
            CType payload;
            if (inner.Type?.Unqualified is CType.Optional { Inner: var optionalPayload }) { payload = optionalPayload; }
            else if (comptimeByType && inner is LitInt or Cast { Operand: LitInt }) { payload = inner.Type ?? CType.Long; }
            else { return false; }
            // A `?comptime_int` payload is an untyped number: bind it as a plain `long` literal.
            if (payload.Unqualified == CType.Int128) { payload = CType.Long; }
            switch (_ir.ResolveComptimeFold(inner))
            {
                case DefaultLit:
                    info = (false, 0, payload);
                    return true;
                case { } lit when _ir.ConstEval(lit) is { } v:
                    info = (true, v, payload);
                    return true;
                default:
                    return false;
            }
        }
        return cur.Content is Zig.Ident id
            && _symbols.Resolve(Tok(id.Arg0)) is { } sym
            && _comptimeOptionalVars.TryGetValue(sym, out info);
    }

    /// <summary>Bind a folded captured-<c>if</c> payload <paramref name="capName"/> to its comptime
    /// literal for the taken then-branch (road-to-zig-std S4b). A narrow UNSIGNED inner type
    /// (<c>u8</c>/<c>u16</c>) is bound as <c>int</c> so the folded literal renders as a bare <c>N</c>
    /// (the value always fits) rather than <c>Nu</c> — a <c>uint</c> literal would not implicitly assign
    /// to a <c>byte</c>/<c>ushort</c> sink (CS0266). Wider / signed inners keep their type. <c>_</c>
    /// binds nothing. The caller wraps this in a fresh scope.</summary>
    private void BindFoldedCapture(string capName, long value, CType inner)
    {
        if (capName == "_") { return; }
        var bindType = inner.Unqualified is CType.Prim { Integer: true, Signed: false, Bytes: <= 2 }
            ? CType.Int
            : inner;
        var capSym = _symbols.Declare(new Symbol { Name = capName, Kind = SymKind.Var, Type = bindType });
        _comptimeVars[capSym] = (value, bindType);
    }

    private CExpr LowerIfCaptureExpr(Item condItem, string capName, Item thenItem, Item elseItem, CType? sink = null,
        string? errCapName = null)
    {
        // Comptime fold (S4b): if the condition is a comptime-known optional (a generic instance's
        // `comptime x: ?T` seed), select the taken branch NOW — no runtime test, no hoist. A comptime
        // `null` yields the else; a comptime-known payload yields the then with `x` bound to the literal.
        if (TryComptimeOptionalCond(condItem, out var copt))
        {
            if (!copt.HasValue) { return LowerCaptureBranch(elseItem, sink, _hoist); }
            _symbols.EnterScope();
            BindFoldedCapture(capName, copt.Value, copt.Inner);
            var folded = LowerCaptureBranch(thenItem, sink, _hoist);
            _symbols.ExitScope();
            return folded;
        }

        var buf = RequireHoistable("value `if (opt) |x| … else …`");
        var savedImpure = _hoistImpureSeen;

        var cond = LowerExpr(condItem);
        var ct = cond.Type.Unqualified;

        // Single-eval the condition (a bare var is already re-readable; anything else binds to a temp).
        CExpr condRef;
        if (cond is VarRef)
        {
            condRef = cond;
        }
        else
        {
            var ctmp = _symbols.Declare(new Symbol { Name = "__ifcapc" + _anfTempCounter++, Kind = SymKind.Var, Type = cond.Type });
            buf.Add(new DeclStmt(new List<LocalDecl> { new(ctmp, cond) }));
            condRef = new VarRef(ctmp) { Type = cond.Type, IsLValue = true };
        }

        CExpr test;
        CExpr payloadInit;
        CType payloadType;
        if (ct is CType.Optional opt)
        {
            test = new Member(condRef, "HasValue", false) { Type = CType.Bool };
            payloadInit = new Member(condRef, "Value", false) { Type = opt.Inner };
            payloadType = opt.Inner;
        }
        else if (ct is CType.Pointer)
        {
            test = condRef;          // Cond.B(void*) tests non-null
            payloadInit = condRef;   // the unwrapped pointer is the same value
            payloadType = cond.Type;
        }
        else if (ct is CType.ErrorUnion eu)
        {
            // An error union (`if (r.getSize()) |size| … else |_| …`): the success payload binds in the then arm, the
            // error code (as in the statement form) in the else arm. A value inspection, never a `try`.
            test = new Unary(UnOp.LogNot, new Member(condRef, "IsErr", false) { Type = CType.Bool }) { Type = CType.Bool };
            payloadInit = new Member(condRef, "Value", false) { Type = eu.Payload };
            payloadType = eu.Payload;
        }
        else
        {
            throw new IrUnsupportedException(
                "zig value-position `if (...) |x| ... else ...` requires an optional, optional-pointer or error-union condition");
        }
        if (errCapName is not null && ct is not CType.ErrorUnion)
        {
            throw new IrUnsupportedException("zig value `if (x) |v| … else |e| …`: only an error union has an error to capture");
        }

        // then-branch: bind the payload to `x`, then lower the then value (which may use `x`).
        var thenStmts = new List<CStmt>();
        _symbols.EnterScope();
        if (capName != "_")
        {
            var capSym = _symbols.Declare(new Symbol { Name = capName, Kind = SymKind.Var, Type = payloadType });
            thenStmts.Add(new DeclStmt(new List<LocalDecl> { new(capSym, payloadInit) }));
        }
        var thenVal = LowerCaptureBranch(thenItem, sink, thenStmts);
        _symbols.ExitScope();

        var elseStmts = new List<CStmt>();
        _symbols.EnterScope();
        if (errCapName is not null && errCapName != "_")
        {
            var errSym = _symbols.Declare(new Symbol { Name = errCapName, Kind = SymKind.Var, Type = CType.ErrorSet });
            elseStmts.Add(new DeclStmt(new List<LocalDecl> { new(errSym, new Member(condRef, "Code", false) { Type = CType.ErrorSet }) }));
        }
        var elseVal = LowerCaptureBranch(elseItem, sink, elseStmts);
        _symbols.ExitScope();
        var resultType = sink ?? thenVal.Type;

        // A result temp (declared before the statement), assigned by each branch of a real `if`.
        var resSym = _symbols.Declare(new Symbol { Name = "__ifcap" + _anfTempCounter++, Kind = SymKind.Var, Type = resultType });
        _hoistImpureSeen = savedImpure;   // the construct's internals are sequenced into the buffer
        buf.Add(new DeclStmt(new List<LocalDecl> { new(resSym, new DefaultLit { Type = resultType }) }));
        var resRef = new VarRef(resSym) { Type = resultType, IsLValue = true };
        thenStmts.Add(new ExprStmt(new Assign(null, resRef, thenVal) { Type = resultType }));
        elseStmts.Add(new ExprStmt(new Assign(null, resRef, elseVal) { Type = resultType }));
        buf.Add(new If(test, new Block(thenStmts), new Block(elseStmts)));
        return new VarRef(resSym) { Type = resultType };
    }

    /// <summary>One arm's value of a value-position capture <c>if</c>, at the result type when there is one.
    /// A labeled value block (<c>if (p.peek(0)) |b| init: { …; break :init .left; } else null</c>, std.fmt's
    /// Placeholder.parse) needs statements: they go to <paramref name="into"/>, ahead of the arm's
    /// assignment, and the value is the block's result temp.</summary>
    private CExpr LowerCaptureBranch(Item item, CType? sink, List<CStmt>? into)
    {
        if (!IsLabeledValue(item)) { return sink is { } s ? LowerExprSink(item, s) : LowerExpr(item); }
        if (into is null) { throw new IrUnsupportedException("a labeled value block as a folded `if` arm needs a statement position"); }
        Symbol? result = null;
        into.Add(LowerLabeledValue(item, sink, temp =>
        {
            result = temp;
            return new Seq(new List<CStmt>());
        }));
        return result is { } r ? new VarRef(r) { Type = r.Type }
            : throw new IrUnsupportedException("internal: a labeled value block produced no result");
    }

    /// <summary>Lower an optional capture-<c>while</c> <c>while (opt) |x| body</c> (Milestone M, part
    /// 2). The condition is re-evaluated EACH iteration (it commonly advances an iterator), so it lives
    /// inside the loop body — a fresh <c>__cap</c> per turn. When it yields a payload, bind <c>x</c> and
    /// run the body; otherwise break. Desugars to
    /// <code>while (true) { var __cap = cond; if (has) { var x = payload; body } else break; }</code>
    /// which produces a real <see cref="While"/> node, so a labeled break/continue composes via the
    /// existing labeled-loop machinery. A value optional <c>?T</c> tests <c>__cap.HasValue</c> / binds
    /// <c>.Value</c>; a niche optional pointer tests non-null / binds the pointer itself. <c>_</c> tests
    /// without binding. An error-union or non-optional condition is a clear error.
    /// <para>A continue-expression (<paramref name="contPost"/>, lowered here so it sees the capture) reads the capture
    /// too (<c>while (it) |n| : (it = n.next)</c>, the std.SinglyLinkedList walk, task #105), so the capture is then
    /// declared in the <c>for</c> INIT, whose scope spans the post and the body, and assigned each turn.</para></summary>
    private CStmt LowerWhileCapture(Item condItem, string capName, Item bodyItem,
        (Item body, string? errName)? elseInfo = null, Func<CExpr>? contPost = null)
    {
        _symbols.EnterScope();
        try
        {
            return LowerWhileCaptureCore(condItem, capName, bodyItem, elseInfo, contPost);
        }
        finally
        {
            _symbols.ExitScope();
        }
    }

    /// <summary>The body of <see cref="LowerWhileCapture"/>, inside the scope that holds a hoisted capture.</summary>
    private CStmt LowerWhileCaptureCore(Item condItem, string capName, Item bodyItem,
        (Item body, string? errName)? elseInfo, Func<CExpr>? contPost)
    {
        var cond = LowerExpr(condItem);
        var ct = cond.Type.Unqualified;
        DeclStmt? hoistedCapture = null;
        // Bind the capture for this turn: a fresh local, or (with a continue-expression) an assignment to the one
        // declared in the `for` init.
        CStmt BindCapture(CType payloadType, CExpr payloadInit)
        {
            if (contPost is null)
            {
                var local = _symbols.Declare(new Symbol { Name = capName, Kind = SymKind.Var, Type = payloadType });
                return new DeclStmt(new List<LocalDecl> { new(local, payloadInit) });
            }
            var hoisted = _symbols.Declare(new Symbol { Name = capName, Kind = SymKind.Var, Type = payloadType });
            hoistedCapture = new DeclStmt(new List<LocalDecl> { new(hoisted, new DefaultLit { Type = payloadType }) });
            return new ExprStmt(new Assign(null, new VarRef(hoisted) { Type = payloadType, IsLValue = true }, payloadInit)
                { Type = payloadType });
        }

        var capTmp = _symbols.Declare(new Symbol { Name = "__cap", Kind = SymKind.Var, Type = cond.Type });
        var capRef = new VarRef(capTmp) { Type = cond.Type, IsLValue = true };

        List<CStmt> loopBody;

        // Error-union capture-while: bind the success payload each turn; on error, bind `e` (the flat
        // `ushort Code`) and run the mandatory `else |e|` branch, then break. Structured like
        // LowerIfCapture's error-union arm (error branch = the C# `if`, success = `else`, so no `!`).
        if (ct is CType.ErrorUnion eu)
        {
            if (elseInfo is not { errName: { } errName, body: var eErrBody })
            {
                throw new IrUnsupportedException(
                    "zig error-union capture `while (eu) |x|` requires an `else |e|` clause to handle the error");
            }
            var okStmts = new List<CStmt>();
            if (capName != "_" && contPost is not null)
            {
                okStmts.Add(BindCapture(eu.Payload, new Member(capRef, "Value", false) { Type = eu.Payload }));
            }
            _symbols.EnterScope();
            if (capName != "_" && contPost is null)
            {
                okStmts.Add(BindCapture(eu.Payload, new Member(capRef, "Value", false) { Type = eu.Payload }));
            }
            okStmts.Add(LowerStmt(bodyItem));
            _symbols.ExitScope();

            var errStmts = new List<CStmt>();
            _symbols.EnterScope();
            if (errName != "_")
            {
                var errSym = _symbols.Declare(new Symbol { Name = errName, Kind = SymKind.Var, Type = CType.ErrorSet });
                errStmts.Add(new DeclStmt(new List<LocalDecl> { new(errSym, new Member(capRef, "Code", false) { Type = CType.ErrorSet }) }));
            }
            errStmts.Add(LowerStmt(eErrBody));
            errStmts.Add(new Break());
            _symbols.ExitScope();

            var isErr = new Member(capRef, "IsErr", false) { Type = CType.Bool };
            loopBody = new List<CStmt>
            {
                new DeclStmt(new List<LocalDecl> { new(capTmp, cond) }),
                new If(isErr, new Block(errStmts), new Block(okStmts)),
            };
        }
        else
        {
            CExpr test;
            CExpr payloadInit;
            CType payloadType;
            if (ct is CType.Optional opt)
            {
                test = new Member(capRef, "HasValue", false) { Type = CType.Bool };
                payloadInit = new Member(capRef, "Value", false) { Type = opt.Inner };
                payloadType = opt.Inner;
            }
            else if (ct is CType.Pointer)
            {
                test = capRef;        // Cond.B(void*) tests non-null
                payloadInit = capRef; // the unwrapped pointer is the same value
                payloadType = cond.Type;
            }
            else
            {
                throw new IrUnsupportedException(
                    "zig `while (...) |x|` requires an optional condition");
            }
            if (elseInfo is { errName: not null })
            {
                throw new IrUnsupportedException(
                    "zig `while (optional) |x| … else |e|`: an optional has no error to capture (use a plain `else`)");
            }

            // then-branch: bind the payload, then the user body, with `x` in scope while lowering it.
            var thenStmts = new List<CStmt>();
            if (capName != "_" && contPost is not null) { thenStmts.Add(BindCapture(payloadType, payloadInit)); }
            _symbols.EnterScope();
            if (capName != "_" && contPost is null) { thenStmts.Add(BindCapture(payloadType, payloadInit)); }
            thenStmts.Add(LowerStmt(bodyItem));
            _symbols.ExitScope();

            // exit branch (payload null): run the `else` body (if any), then break. Kept a bare
            // `break` when there's no else, preserving the plain capture-while emit shape.
            CStmt exitBranch;
            if (elseInfo is { body: var elseBody })
            {
                exitBranch = new Block(new List<CStmt> { LowerStmt(elseBody), new Break() });
            }
            else
            {
                exitBranch = new Break();
            }

            loopBody = new List<CStmt>
            {
                new DeclStmt(new List<LocalDecl> { new(capTmp, cond) }),
                new If(test, new Block(thenStmts), exitBranch),
            };
        }

        // body: re-eval the condition each turn (via the fresh __cap), then bind+run or exit. A
        // continue-expression (`: (cont)`) lowers to the C `For` post, so `continue` runs the cont.
        var trueLit = new LitBool(true) { Type = CType.Bool };
        return contPost is null
            ? new While(trueLit, new Block(loopBody))
            : new For(hoistedCapture, trueLit, contPost(), new Block(loopBody));
    }

    /// <summary>Dispatch a <c>switch</c> statement: lower the subject once, then route a
    /// tagged-union subject (a value or pointer-to a registered <c>union(enum)</c>) to
    /// <see cref="LowerUnionSwitch"/> (the tag-discriminant + payload-capture path) and any other
    /// subject to the plain <see cref="LowerSwitch"/>.</summary>
    /// <summary>Each container const whose address was taken, by (container, name): its static global.</summary>
    private readonly Dictionary<(string Container, string Name), Symbol> _staticContainerConsts = new();

    /// <summary>The address of a container const named by <paramref name="operand"/> (a bare sibling const, or
    /// <c>Container.name</c>), in static storage: <c>&amp;vtable</c> in std.Io.Writer.Allocating's <c>.vtable = &amp;vtable</c>.
    /// The const was re-lowered as a VALUE at each use, so its address was a copy on the current frame, which dangles
    /// once that frame returns (a silent crash). Memoized, so every <c>&amp;</c> is the same address, as in zig. Null when
    /// the operand is not a container const, or its value is not a constant initializer.</summary>
    private CExpr? TryStaticContainerConstAddress(Item operand)
    {
        (string Container, string Name, Item? TypeItem, Item Rhs)? found = null;
        if (operand.Content is Zig.Ident id && _symbols.Resolve(Tok(id.Arg0)) is null)
        {
            for (var cc = _currentConstContainer ?? _currentContainer; cc is not null; cc = _containerParents.GetValueOrDefault(cc))
            {
                if (_containerConsts.TryGetValue(cc, out var sibs) && sibs.TryGetValue(Tok(id.Arg0), out var sib))
                {
                    found = (cc, Tok(id.Arg0), sib.typeItem, sib.rhs);
                    break;
                }
            }
        }
        else if (operand.Content is Zig.Field { Arg0.Content: Zig.Ident baseId } f && _symbols.Resolve(Tok(baseId.Arg0)) is null
                 && TryLookupContainerType(Tok(baseId.Arg0), out var baseType) && ContainerTypeName(baseType) is { } cn
                 && _containerConsts.TryGetValue(cn, out var consts) && consts.TryGetValue(Tok(f.Arg2), out var member))
        {
            found = (cn, Tok(f.Arg2), member.typeItem, member.rhs);
        }
        if (found is not var (container, name, typeItem, rhs)) { return null; }
        if (!_staticContainerConsts.TryGetValue((container, name), out var sym))
        {
            var value = LowerContainerConst(container, name, typeItem, rhs);
            if (value.Type.Unqualified is not CType.Named || !IsStaticInitializer(value)) { return null; }
            sym = _symbols.Declare(new Symbol
            {
                Name = $"{container}__{name}__static", Kind = SymKind.Var, Type = value.Type, Storage = Storage.Static, IsGlobal = true,
            });
            _ir.Globals.Add(new GlobalVar(sym, value));
            sym.AddressTaken = true;
            _staticContainerConsts[(container, name)] = sym;
        }
        return new Unary(UnOp.AddrOf, new VarRef(sym) { Type = sym.Type, IsLValue = true }) { Type = new CType.Pointer(sym.Type) };
    }

    /// <summary>Lower <c>&amp;.{ … }</c> result-located at a pointer to <paramref name="pointee"/>. zig puts a
    /// comptime-known literal in static storage (every evaluation yields the same address), so its
    /// struct value becomes a synthesized static global and the expression its address: std's VTable
    /// instances (<c>.vtable = &amp;.{ .drain = fixedDrain, .flush = noopFlush }</c>). A literal with a
    /// runtime field (a stack temporary in zig) is a loud cut.</summary>
    private CExpr LowerAddressOfStructLiteral(Item literal, CType pointee)
    {
        var value = LowerExprSink(literal, pointee.Unqualified);
        if (!IsStaticInitializer(value))
        {
            throw new IrUnsupportedException(
                "zig `&.{ … }` with a runtime-known field (a pointer to a stack temporary) is not supported yet; "
                + "a comptime-known one (constants, function names) is");
        }
        var sym = _symbols.Declare(new Symbol
        {
            Name = $"{_modulePrefix ?? "root"}__anon{_anonStaticCounter++}", Kind = SymKind.Var, Type = value.Type,
            Storage = Storage.Static, IsGlobal = true,
        });
        _ir.Globals.Add(new GlobalVar(sym, value));
        sym.AddressTaken = true;
        var global = new VarRef(sym) { Type = value.Type, IsLValue = true };
        return new Unary(UnOp.AddrOf, global) { Type = new CType.Pointer(pointee) };
    }

    /// <summary>Counter for the static globals <see cref="LowerAddressOfStructLiteral"/> synthesizes.</summary>
    private int _anonStaticCounter;

    /// <summary>True when <paramref name="e"/> is comptime-known data a static initializer can hold: a
    /// literal, an enum constant, a function address, a default, or a struct literal of those.</summary>
    private static bool IsStaticInitializer(CExpr e) => e switch
    {
        LitInt or LitFloat or LitBool or EnumConstRef or NullPtr or DefaultLit => true,
        VarRef { Sym.Kind: SymKind.Func } => true,
        Unary { Op: UnOp.AddrOf, Operand: VarRef { Sym.Kind: SymKind.Func } } => true,
        Cast c => IsStaticInitializer(c.Operand),
        Paren p => IsStaticInitializer(p.Inner),
        ComptimeFold { Resolved: { } r } => IsStaticInitializer(r),
        StructInit si => si.Members.All(m => IsStaticInitializer(m.Value)),
        _ => false,
    };

    /// <summary>Lower an assignment statement <c>lhs = rhs;</c> (also a prong body <c>v =&gt; lhs = rhs</c>): a
    /// discard <c>_ = e</c>, a value-block / value-control-flow RHS temp-filled against the lvalue, or a plain
    /// store with the lvalue's type as the sink. Under an ANF hoist buffer, like every statement.</summary>
    /// <summary>The zig bindings that are immutable, <c>const</c> locals and globals (a parameter is immutable too, and is
    /// known by its kind): a store to one, or through a pointer to one, is "cannot assign to constant" (task #95).</summary>
    private readonly HashSet<Symbol> _zigConstBindings = new();

    /// <summary>Reject a store whose target zig cannot write (task #95): a <c>const</c> binding or parameter, a field or array
    /// element of one, or anything reached through a pointer or slice to const (<c>&amp;y</c> of a const <c>y</c> is a
    /// <c>*const T</c>). Always returns null (so it chains with <c>??</c>): a legal store lowers normally.</summary>
    private CStmt? RejectConstStore(Item targetItem)
    {
        if (targetItem.Content is Zig.Ident discard && Tok(discard.Arg0) == "_") { return null; }
        CExpr target;
        using (EnterThrowawayHoist()) { target = LowerExpr(targetItem); }
        if (IsConstStorage(target))
        {
            throw new CompileException("zig: cannot assign to constant"
                + (Unparen(target) is VarRef v ? $" '{v.Sym.Name}'" : ""));
        }
        return null;
    }

    /// <summary>True when the lvalue names storage zig treats as immutable (see <see cref="RejectConstStore"/>).</summary>
    private bool IsConstStorage(CExpr lvalue) => lvalue switch
    {
        Paren p => IsConstStorage(p.Inner),
        // `p[1] = v` with `p: *[2]u32` indexes the array `p` POINTS AT (PointedArray retypes the pointer to its array), so
        // the storage is the pointee, not the binding: a `const p` or a parameter still writes through.
        _ when PointedArrayPointee(lvalue) is { } pointee => pointee.IsConst,
        VarRef { Sym: var s } => _zigConstBindings.Contains(s) || s.Kind == SymKind.Param,
        Member { Arrow: true } m => m.Base.Type.Unqualified is CType.Pointer { Pointee.IsConst: true },
        Member m => IsConstStorage(m.Base),
        DotCC.Ir.Index ix => ix.Base.Type.Unqualified switch
        {
            CType.Array => IsConstStorage(ix.Base),
            CType.Pointer ptr => ptr.Pointee.IsConst,
            CType.Slice sl => sl.Element.IsConst,
            _ => false,
        },
        Unary { Op: UnOp.Deref, Operand.Type: var pt } => pt.Unqualified is CType.Pointer { Pointee.IsConst: true },
        _ => false,
    };

    /// <summary>The pointee of a pointer-to-array expression that lowering retyped to the array it points at (a variable or
    /// field declared <c>*[N]T</c>, typed <c>[N]T</c> here), or null when the expression is what it was declared as.</summary>
    private CType? PointedArrayPointee(CExpr e)
    {
        if (e.Type.Unqualified is not CType.Array) { return null; }
        CType? declared = e switch
        {
            VarRef v => v.Sym.Type,
            Member { Arrow: true } am when am.Base.Type.Unqualified is CType.Pointer { Pointee: var owner } => _ir.StructFieldType(owner, am.Field),
            Member m => _ir.StructFieldType(m.Base.Type, m.Field),
            _ => null,
        };
        return declared?.Unqualified is CType.Pointer { Pointee: var pointee } && pointee.Unqualified is CType.Array ? pointee : null;
    }

    /// <summary>An assignment then-arm of an <c>if</c> with an <c>else</c> (<c>if (c) i += 1 else i -= 1;</c>, task #96): the
    /// same lowering as the assignment statement its operator spells.</summary>
    private CStmt LowerAssignArm(Zig.AssignArm arm) => Tok(arm.Arg1) switch
    {
        "=" => LowerAssignStmt(arm.Arg0, arm.Arg2),
        "+=" or "+%=" => CompoundAssign(arm.Arg0, BinOp.Add, arm.Arg2),
        "-=" or "-%=" => CompoundAssign(arm.Arg0, BinOp.Sub, arm.Arg2),
        "*=" or "*%=" => CompoundAssign(arm.Arg0, BinOp.Mul, arm.Arg2),
        "/=" => CompoundAssign(arm.Arg0, BinOp.Div, arm.Arg2),
        "%=" => CompoundAssign(arm.Arg0, BinOp.Mod, arm.Arg2),
        "<<=" => CompoundAssign(arm.Arg0, BinOp.Shl, arm.Arg2),
        ">>=" => CompoundAssign(arm.Arg0, BinOp.Shr, arm.Arg2),
        "&=" => CompoundAssign(arm.Arg0, BinOp.BitAnd, arm.Arg2),
        "|=" => CompoundAssign(arm.Arg0, BinOp.BitOr, arm.Arg2),
        "^=" => CompoundAssign(arm.Arg0, BinOp.BitXor, arm.Arg2),
        "+|=" => SatCompoundAssign(arm.Arg0, "SatAdd", arm.Arg2),
        "-|=" => SatCompoundAssign(arm.Arg0, "SatSub", arm.Arg2),
        "*|=" => SatCompoundAssign(arm.Arg0, "SatMul", arm.Arg2),
        var op => throw new IrUnsupportedException($"zig: assignment operator `{op}` in an if arm"),
    };

    private CStmt LowerAssignStmt(Item lhsItem, Item rhsItem)
        => TryAssignComptimeVar(lhsItem, null, rhsItem) ?? RejectConstStore(lhsItem) ?? Hoisted(() =>
        {
            if (lhsItem.Content is Zig.Ident lhs && Tok(lhs.Arg0) == "_")
            {
                // `_ = a catch {};` / `_ = a orelse break;` — the value is DISCARDED, so the
                // fallback arm needs no payload (a void block is fine here, as in zig).
                if (IsControlFlowFallback(rhsItem, out var dL, out var dC, out var dCap, out var dArm))
                {
                    return LowerControlFlowFallback(dL, dC, dCap, dArm, null);
                }
                var discarded = LowerExpr(rhsItem);
                // `_ = ctx;` over a `void` value (an unused `context: void` parameter) has nothing to
                // evaluate and no C# spelling: it emits nothing.
                if (discarded.Type.Unqualified is CType.VoidType && IsErasableVoid(discarded))
                {
                    return new Seq(new List<CStmt>());
                }
                return new ExprStmt(discarded);
            }
            // `buf[index..][0..2].* = std.fmt.digits2(…);` (std.Io.Writer.printIntAny): the deref of a slice is
            // the ARRAY it views, so storing an array into it copies the elements, as `@memcpy` does.
            if (lhsItem.Content is Zig.Deref { Arg0: var viewedItem } && IsSliceOperand(viewedItem))
            {
                var copyDest = LowerMemSlice(viewedItem, wantConst: false, out var copyElem);
                var copySrc = LowerMemSlice(rhsItem, wantConst: true, out _);
                return new ExprStmt(new ZigMemCall("CopyForwards", copyElem, new List<CExpr> { copyDest, copySrc }) { Type = CType.Void });
            }
            // `buffer.* = @bitCast(value)` with `buffer: *[N]u8` (std.mem.writeInt): the value's BYTES stored into the array
            // the pointer names. (It was once a store to the pointer itself, `buffer = BitCast<ulong, byte*>(value)`.)
            if (lhsItem.Content is Zig.Deref { Arg0: var arrayPtrItem }
                && rhsItem.Content is Zig.BuiltinCall { Arg0: var bitCastTok } bitCast && Tok(bitCastTok) == "@bitCast"
                && Flatten(bitCast.Arg2) is [var bitsItem])
            {
                bool pointsAtArray;
                using (EnterThrowawayHoist())
                {
                    pointsAtArray = LowerExpr(arrayPtrItem).Type.Unqualified is CType.Pointer { Pointee.Unqualified: CType.Array };
                }
                if (pointsAtArray)
                {
                    return new ExprStmt(new Call("System.Runtime.CompilerServices.Unsafe.WriteUnaligned",
                        new List<CExpr> { LowerExpr(arrayPtrItem), LowerExpr(bitsItem) }) { Type = CType.Void });
                }
            }
            var target = LowerExpr(lhsItem);
            // `d = a;` between arrays: an element copy (the C# rep is the element pointer, so a plain assignment
            // would alias the storage). `d = undefined;` changes nothing.
            if (target.Type.Unqualified is CType.Array { Count: { } assignCount } assignArr && target is VarRef or Member)
            {
                var assigned = LowerExprSink(rhsItem, assignArr);
                if (assigned is DefaultLit) { return new Seq(new List<CStmt>()); }
                return new ExprStmt(ArrayElementCopy(target, assigned, assignArr, assignCount));
            }
            // `x = blk: { … break :blk v; };` — a labeled value-block assignment (Milestone L,
            // part 2): temp-fill against the lvalue's type, then assign the result temp into it.
            if (IsLabeledValue(rhsItem))
            {
                return LowerLabeledValue(rhsItem, target.Type,
                    temp => new ExprStmt(new Assign(null, target, new VarRef(temp) { Type = temp.Type }) { Type = target.Type }));
            }
            // `x = switch (y) { … blk: {…} };` / `x = if (c) blk:{…} else …;` — a value-position
            // if/switch with a statement-producing branch (Milestone Y, part 1): temp-fill against
            // the lvalue's type, then assign the result temp into it.
            if (IsValueControlFlowStmt(rhsItem))
            {
                return LowerValueControlFlowStmt(rhsItem, target.Type,
                    temp => new ExprStmt(new Assign(null, target, new VarRef(temp) { Type = temp.Type }) { Type = target.Type }));
            }
            var value = LowerExprSink(rhsItem, target.Type);   // target type is the sink (`x = .member;`)
            // Storing a void value into void storage (`unit = {};`) moves no data.
            if (target.Type.Unqualified is CType.VoidType && IsErasableVoid(value)) { return new Seq(new List<CStmt>()); }
            return new ExprStmt(new Assign(null, target, value) { Type = target.Type });
        });

    /// <summary>Lower a statement switch's bare-expression prong body. A nested <c>switch</c> there is itself a
    /// STATEMENT (std.math.sqrt's <c>.int =&gt; |I| switch (I.signedness) { .unsigned =&gt; return …, … }</c>), so
    /// its prongs may return or raise, which a switch EXPRESSION's may not; anything else is an expression
    /// statement.</summary>
    private CStmt LowerProngExprStmt(Item e) => e.Content switch
    {
        // In a LABELED switch (`r: switch (x) { 0 => 10, … }`), a bare value prong is the switch's value: `break :r 10`.
        // (`unreachable` stays the trap it is.)
        _ when _activeSwitchValueLabel is { } valueLabel && !IsUnreachableItem(e) => LowerLabeledBreak(valueLabel, e),
        Zig.SwitchExpr s => LowerSwitchStmt(s.Arg2, s.Arg5),
        Zig.SwitchExprTrailing s => LowerSwitchStmt(s.Arg2, s.Arg5),
        _ => new ExprStmt(LowerExpr(e)),
    };

    /// <summary>The label a labeled switch hands to its OWN switch statement (<see cref="LowerLabeledValue"/>), taken
    /// by that switch as it starts, so no switch nested in one of its prongs inherits it.</summary>
    private string? _pendingSwitchValueLabel;

    /// <summary>The label of the labeled switch whose prongs are being lowered (null inside any other switch):
    /// its bare value prongs break to it (<see cref="LowerProngExprStmt"/>).</summary>
    private string? _activeSwitchValueLabel;

    /// <summary>True when <paramref name="e"/> is the bare <c>unreachable</c>.</summary>
    private static bool IsUnreachableItem(Item e) => e.Content is Zig.Ident { Arg0: var tok } && Tok(tok) == "unreachable";

    /// <summary>How many <c>inline</c> loops are being unrolled around the current statement.</summary>
    private int _inlineUnrollDepth;

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
        { IfSwitch: { } isw } => LowerProngIfSwitch(isw),
        { IfBlock: { } ib } => LowerProngIfBlock(ib),
        { IfCaptureReturn: { } icr } => LowerIfCapture(icr.Arg4, Tok(icr.Arg7), icr.Arg9, null, null),
        { Loop: { } loop } => LowerStmt(loop),
        _ => new Seq(new List<CStmt>()),
    };

    /// <summary>A <c>=&gt; if (c) { … }</c> prong body (std.crypto.sha2's <c>.x86_64 =&gt; if (… comptime
    /// builtin.cpu.hasAll(.x86, &amp;.{ .sha, .avx2 })) { … asm … },</c>): a comptime-known condition keeps the block or
    /// nothing, so a target path dotcc cannot take (inline assembly) is never lowered.</summary>
    private CStmt LowerProngIfBlock(Zig.ProngIfBlock p)
    {
        // A condition the comptime questions settle, or one that lowers to a constant (`comptime builtin.cpu.hasAll(…)`
        // folds while it lowers, and `builtin.zig_backend != .stage2_c` is an enum compare): only the taken block lowers.
        if ((TryFoldComptimeCondition(p.Arg4) ?? TryFoldTypeIfCondition(p.Arg4)) is { } taken)
        {
            return taken ? LowerBlock(p.Arg6) : new Seq(new List<CStmt>());
        }
        var cond = LowerExpr(p.Arg4);
        return new If(cond, LowerBlock(p.Arg6), null);
    }

    /// <summary>A <c>=&gt; if (c) switch (s) { … }</c> prong body: the switch statement under an else-less <c>if</c>.
    /// A comptime-known condition keeps only the switch or nothing, so an untaken switch is never analysed.</summary>
    private CStmt LowerProngIfSwitch(Zig.ProngIfSwitch p)
    {
        var (subject, prongs) = p.Arg6.Content switch
        {
            Zig.SwitchExpr s => (s.Arg2, s.Arg5),
            Zig.SwitchExprTrailing s => (s.Arg2, s.Arg5),
            _ => throw new IrUnsupportedException("zig `=> if (c) switch …` prong: " + (p.Arg6.Content?.GetType().Name ?? "null")),
        };
        if (TryFoldComptimeCondition(p.Arg4) is { } taken)
        {
            return taken ? LowerSwitchStmt(subject, prongs) : new Seq(new List<CStmt>());
        }
        var cond = LowerExpr(p.Arg4);
        return new If(cond, new Block(new List<CStmt> { LowerSwitchStmt(subject, prongs) }), null);
    }

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
    /// the plain form and its capture twin (<c>.on =&gt; |v| total += v</c>, task #109).</summary>
    private CStmt LowerAssignProngBody(Item lhs, Item opItem, Item rhs)
        => CompoundOpOf(opItem) is { } op ? CompoundAssign(lhs, op, rhs) : LowerAssignStmt(lhs, rhs);

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

    /// <summary>True when a callee names <c>assert</c> (a bare alias, <c>const assert = std.debug.assert;</c>,
    /// or a dotted <c>std.debug.assert</c>).</summary>
    private static bool IsAssertCallee(Item callee) => callee.Content switch
    {
        Zig.Ident id => Tok(id.Arg0) == "assert",
        Zig.Field f => Tok(f.Arg2) == "assert",
        _ => false,
    };

    /// <summary>True for a runtime loop statement (every <c>LoopStmt</c> form), which gets an unlabeled
    /// break target (<see cref="LowerLoopWithBreakTarget"/>).</summary>
    private static bool IsRuntimeLoopStmt(object? content) => content is
        Zig.StmtWhile or Zig.StmtWhileCont or Zig.StmtWhileContAssign or Zig.StmtWhileContBlock
        or Zig.StmtWhileCapture or Zig.StmtWhileCaptureElse or Zig.StmtWhileCaptureErrElse
        or Zig.StmtWhileCaptureCont or Zig.StmtWhileCaptureContAssign
        or Zig.StmtForRange or Zig.StmtForSlice or Zig.StmtForSliceRef or Zig.StmtForSliceIdx
        or Zig.StmtForSliceIdxRef or Zig.StmtForSlicePair or Zig.StmtForSlicePairTrail
        or Zig.StmtForSliceTriple or Zig.StmtForSliceTripleTrail
        or Zig.StmtForPairRefRef or Zig.StmtForPairRefVal or Zig.StmtForPairValRef;

    /// <summary>Lower a runtime loop with an unlabeled break target (<see cref="LoopBreakTarget"/>), so a
    /// <c>break</c> inside a <c>switch</c> in its body exits the loop, as in zig. The label is emitted
    /// after the loop only when such a break used it; otherwise the loop lowers exactly as before.</summary>
    private CStmt LowerLoopWithBreakTarget(Item loopItem)
    {
        var t = new LoopBreakTarget { BreakLabel = "__loop" + _loopLabelCounter++ + "_swbrk" };
        _loopBreakTargets.Push(t);
        _loopBeingWrapped = loopItem;
        CStmt loop;
        try { loop = LowerStmt(loopItem); }
        finally { _loopBreakTargets.Pop(); }
        return t.Used
            ? new Seq(new List<CStmt> { loop, new Labeled(t.BreakLabel, new Block(new List<CStmt>())) })
            : loop;
    }

    /// <summary>An unlabeled <c>break</c>: a plain C# <c>break</c>, unless a <c>switch</c> statement sits
    /// between it and its loop, where it is a <c>goto</c> past the loop (<see cref="LoopBreakTarget"/>).</summary>
    private CStmt LowerUnlabeledBreak()
    {
        if (_loopBreakTargets.TryPeek(out var t) && t.SwitchDepth > 0)
        {
            t.Used = true;
            return new Goto(t.BreakLabel);
        }
        return new Break();
    }

    /// <summary>Lower a <c>switch</c> statement, counting it as a barrier for an unlabeled <c>break</c>
    /// in its prongs (<see cref="LoopBreakTarget"/>).</summary>
    private CStmt LowerSwitchStmt(Item subjectItem, Item prongsItem)
    {
        var previousLabel = _activeSwitchValueLabel;
        _activeSwitchValueLabel = _pendingSwitchValueLabel;   // this switch's own label, or null for any other switch
        _pendingSwitchValueLabel = null;
        try { return WithSwitchBarrier(() => LowerSwitchStmtCore(subjectItem, prongsItem)); }
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
        _ => throw new IrUnsupportedException("zig switch jump prong: " + (jump.Content?.GetType().Name ?? "null")),
    };

    private CStmt LowerSwitchStmtCore(Item subjectItem, Item prongsItem)
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
        return LowerSwitch(subject, prongsItem);
    }

    /// <summary>Lower a non-union <c>switch (subject) { prong, … }</c> to the C IR
    /// <see cref="Switch"/>. Each prong (<c>CaseVals =&gt; Block</c>) becomes a
    /// <see cref="SwitchSection"/>: its case values are the labels (<c>else</c> → the null
    /// default label), and its braced block is the body. Zig switch has NO fall-through, so a
    /// terminating <see cref="Break"/> is appended to any section that doesn't already end
    /// control flow — otherwise the C# backend would synthesize C's fall-through jump. A payload
    /// capture <c>|x|</c> here is an error (only a tagged-union switch binds a payload).</summary>
    private CStmt LowerSwitch(CExpr subject, Item prongsItem)
    {
        var sections = new List<SwitchSection>();
        foreach (var prongItem in Flatten(prongsItem))
        {
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
                case Zig.ProngIfSwitch pis:  caseVals = pis.Arg0; body = new List<CStmt> { LowerProngIfSwitch(pis) }; break;
                case Zig.ProngIfBlock pib:   caseVals = pib.Arg0; body = new List<CStmt> { LowerProngIfBlock(pib) }; break;
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
        return new Switch(subject, sections);
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
        CStmt ProngValue(Item valueItem) => fillValue is { } fill ? fill(valueItem) : new ExprStmt(LowerExpr(valueItem));
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
            // A bare-expr prong (`=> expr`) in a union STATEMENT switch is an expression statement,
            // with no payload capture (capture needs a braced block); handle it up front.
            if (prongItem.Content is Zig.ProngExpr pe)
            {
                RejectUnionRange(pe.Arg0, info);
                var exprLabels = LowerCaseVals(pe.Arg0, info.TagType);
                var peBody = new List<CStmt> { ProngValue(pe.Arg2) };
                if (!EndsInJump(peBody)) { peBody.Add(new Break()); }
                sections.Add(new SwitchSection(exprLabels, peBody));
                continue;
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
                continue;
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
            RejectUnionRange(caseVals, info);
            var labels = LowerCaseVals(caseVals, info.TagType);   // `.variant` → EnumConstRef(U_Tag.variant)

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
                var variant = CaptureVariantName(caseVals, info, captureName);
                var payloadType = info.Variants[variant]
                    ?? throw new IrUnsupportedException(
                        $"union '{info.Name}' variant '{variant}' is a void variant — it has no payload to capture with `|{captureName}|`");
                _symbols.EnterScope();
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
                _symbols.ExitScope();
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

    /// <summary>Lower a for-over-slice — <c>for (s) |x| body</c> and (when <paramref name="index"/>
    /// is set) <c>for (s, START..) |x, i| body</c> — to the C IR <c>for</c>:
    /// <code>{ var __s = s; for (usize __i = 0; __i &lt; __s.Len; __i++) { var x = __s.Ptr[__i];
    /// [var i = __i + START;] body } }</code>
    /// The element capture <c>x</c> is a per-iteration copy (Zig's by-value <c>|x|</c>; the by-ref
    /// <c>|*x|</c> form is deferred). The slice is hoisted to <c>__s</c> unless it is already a bare
    /// variable, so <c>.Len</c>/<c>.Ptr</c> aren't re-evaluated with side effects.</summary>
    /// <summary>Lower a runtime multi-object <c>for (a, b, c) |x, y, z| body</c> (road-to-zig-std G5): one
    /// index walks every object in lockstep, each capture a per-iteration copy of its object's element.
    /// Each object is a slice (an array coerces to one) read once into a temp. zig asserts the lengths are
    /// equal; dotcc walks the FIRST object's length and does not check, its ReleaseFast stance on safety
    /// checks. A <c>_</c> capture binds nothing.</summary>
    private CStmt LowerForParallel(IReadOnlyList<Item> objectItems, IReadOnlyList<string> captures, Item bodyItem,
        IReadOnlyList<bool>? byRef = null)
    {
        var pre = new List<CStmt>();
        var slices = new List<(CExpr Ref, CType.Slice Type)>(objectItems.Count);
        foreach (var item in objectItems)
        {
            var value = LowerExpr(item);
            // An array, or a pointer to one (`&used`), walks as a slice over it.
            if (value.Type.Unqualified is CType.Array arr) { value = CoerceToSlice(value, new CType.Slice(arr.Element)); }
            else if (value.Type.Unqualified is CType.Pointer { Pointee: var pte } && pte.Unqualified is CType.Array parr)
            {
                value = CoerceToSlice(value, new CType.Slice(parr.Element));
            }
            if (value.Type.Unqualified is not CType.Slice slc)
            {
                throw new IrUnsupportedException(
                    $"zig multi-object `for`: each object must be a slice or an array; got {value.Type.Describe()}");
            }
            CExpr sliceRef = value;
            if (value is not VarRef)
            {
                var tmp = _symbols.Declare(new Symbol { Name = "__s", Kind = SymKind.Var, Type = value.Type });
                pre.Add(new DeclStmt(new List<LocalDecl> { new(tmp, value) }));
                sliceRef = new VarRef(tmp) { Type = value.Type, IsLValue = true };
            }
            slices.Add((sliceRef, slc));
        }
        _symbols.EnterScope();
        var iSym = _symbols.Declare(new Symbol { Name = "__i", Kind = SymKind.Var, Type = CType.ULong });
        var iRef = new VarRef(iSym) { Type = CType.ULong, IsLValue = true };
        var init = new DeclStmt(new List<LocalDecl> { new(iSym, new LitInt("0", 0) { Type = CType.ULong }) });
        var len = new Member(slices[0].Ref, "Len", false) { Type = CType.ULong, IsLValue = true };
        var cond = new Binary(BinOp.Lt, iRef, len) { Type = CType.Int };
        var post = new Unary(UnOp.PostInc, iRef) { Type = CType.ULong };
        var bodyStmts = new List<CStmt>();
        for (var k = 0; k < slices.Count; k++)
        {
            if (captures[k] == "_") { continue; }
            var (sref, st) = slices[k];
            var ptr = new Member(sref, "Ptr", false) { Type = new CType.Pointer(st.Element) };
            var elem = new DotCC.Ir.Index(ptr, iRef) { Type = st.Element, IsLValue = true };
            // `|*x|` binds a pointer INTO the object (`x.* = …` writes through), `|x|` a per-iteration copy.
            var capType = byRef is { } br && br[k] ? new CType.Pointer(st.Element) : st.Element;
            CExpr capInit = capType is CType.Pointer ? new Unary(UnOp.AddrOf, elem) { Type = capType } : elem;
            var sym = _symbols.Declare(new Symbol { Name = captures[k], Kind = SymKind.Var, Type = capType });
            bodyStmts.Add(new DeclStmt(new List<LocalDecl> { new(sym, capInit) }));
        }
        bodyStmts.Add(LowerStmt(bodyItem));
        _symbols.ExitScope();
        var forStmt = new For(init, cond, post, new Block(bodyStmts));
        if (pre.Count == 0) { return forStmt; }
        pre.Add(forStmt);
        return new Block(pre);
    }

    private CStmt LowerForSlice(CExpr sliceExpr, string elemName, (string name, CExpr start)? index, Item bodyItem, bool byRef,
        int? elemBits = null)
    {
        // `for (&arr, 0..) |*e, i|` (std.simd.iota) / `for (arr) |x|`: an array, or the address of one, is walked
        // as a slice over it, so a by-reference capture writes the array's elements.
        CType? walkedElem = sliceExpr.Type.Unqualified is CType.Array walkedArray ? walkedArray.Element
            : PointedArray(sliceExpr) is (_, { Element: var pointedElem }) ? pointedElem
            : null;
        if (sliceExpr.Type.Unqualified is not CType.Slice && walkedElem is { } elemToWalk)
        {
            sliceExpr = CoerceToSlice(sliceExpr, new CType.Slice(elemToWalk));
        }
        if (sliceExpr.Type.Unqualified is not CType.Slice slc)
        {
            throw new IrUnsupportedException($"for-over-slice needs a slice; got {sliceExpr.Type.Describe()}");
        }
        var pre = new List<CStmt>();
        CExpr sliceRef;
        if (sliceExpr is VarRef)
        {
            sliceRef = sliceExpr;
        }
        else
        {
            var tmp = _symbols.Declare(new Symbol { Name = "__s", Kind = SymKind.Var, Type = sliceExpr.Type });
            pre.Add(new DeclStmt(new List<LocalDecl> { new(tmp, sliceExpr) }));
            sliceRef = new VarRef(tmp) { Type = sliceExpr.Type, IsLValue = true };
        }

        _symbols.EnterScope();
        // usize __i = 0; __i < __s.Len; __i++
        var iSym = _symbols.Declare(new Symbol { Name = "__i", Kind = SymKind.Var, Type = CType.ULong });
        var iRef = new VarRef(iSym) { Type = CType.ULong, IsLValue = true };
        var init = new DeclStmt(new List<LocalDecl> { new(iSym, new LitInt("0", 0) { Type = CType.ULong }) });
        var lenMember = new Member(sliceRef, "Len", false) { Type = CType.ULong, IsLValue = true };
        var cond = new Binary(BinOp.Lt, iRef, lenMember) { Type = CType.Int };
        var post = new Unary(UnOp.PostInc, iRef) { Type = CType.ULong };

        // body: prepend the element binding and, for the index form, `var i = __i + START;`.
        // By-value (`|x|`): `var x = __s.Ptr[__i];` (a per-iteration copy). By-reference (`|*x|`):
        // `T* x = &(__s.Ptr[__i]);` so `x.* = …` writes through to the element.
        var ptrMember = new Member(sliceRef, "Ptr", false) { Type = new CType.Pointer(slc.Element) };
        var elemAccess = new DotCC.Ir.Index(ptrMember, iRef) { Type = slc.Element, IsLValue = true };
        var elemType = byRef ? new CType.Pointer(slc.Element) : slc.Element;
        CExpr elemInit = byRef ? new Unary(UnOp.AddrOf, elemAccess) { Type = elemType } : elemAccess;
        var elemSym = _symbols.Declare(new Symbol { Name = elemName, Kind = SymKind.Var, Type = elemType });
        if (!byRef) { RecordValueBits(elemSym, elemBits, null); }
        var bodyStmts = new List<CStmt> { new DeclStmt(new List<LocalDecl> { new(elemSym, elemInit) }) };
        if (index is { } idx)
        {
            var idxInit = new Binary(BinOp.Add, iRef, new Cast(CType.ULong, idx.start) { Type = CType.ULong }) { Type = CType.ULong };
            var idxSym = _symbols.Declare(new Symbol { Name = idx.name, Kind = SymKind.Var, Type = CType.ULong });
            bodyStmts.Add(new DeclStmt(new List<LocalDecl> { new(idxSym, idxInit) }));
        }
        bodyStmts.Add(LowerStmt(bodyItem));
        _symbols.ExitScope();

        var forStmt = new For(init, cond, post, new Block(bodyStmts));
        if (pre.Count == 0) { return forStmt; }
        pre.Add(forStmt);
        return new Block(pre);
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
        var subject = LowerExpr(subjectItem);
        var arms = new List<SwitchExprArm>();
        foreach (var prongItem in Flatten(prongsItem))
        {
            if (prongItem.Content is not Zig.ProngExpr pe)
            {
                throw new IrUnsupportedException(
                    "zig switch-expression prong must yield a value (`v => expr`); a block-bodied prong " +
                    "(a labeled `break :blk v`) is supported only as a full `const`/`var`/`return`/assignment RHS " +
                    "(Milestone Y, part 1), not in a sub-expression; a `|x|` capture in a switch expression is not supported yet");
            }
            var value = LowerExprSink(pe.Arg2, sink);
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
        if (prongs.All(p => p.Content is Zig.ProngExpr) && !prongs.Any(p => p.Content is Zig.ProngExpr { Arg2.Content: Zig.BuiltinCall cb }
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
        Zig.IfExpr e             => IsLabeledValue(e.Arg4) || IsLabeledValue(e.Arg6),
        Zig.SwitchExpr s         => SwitchExprNeedsStmt(s.Arg5, s.Arg2),
        Zig.SwitchExprTrailing s => SwitchExprNeedsStmt(s.Arg5, s.Arg2),
        // `comptime switch` / `comptime if`: the inner form decides. The `comptime` asks zig to evaluate
        // it at compile time; dotcc's lowering already folds the arm whenever the subject is
        // comptime-known, and a runtime subject (which zig rejects here) keeps its runtime lowering.
        Zig.ComptimeSwitchExpr c => IsValueControlFlowStmt(c.Arg1),
        Zig.ComptimeIfExpr c     => IsValueControlFlowStmt(c.Arg1),
        // A value-position loop (`while/for … else`, Milestone Y part 2) ALWAYS needs the statement
        // lowering — a loop that yields via `break v` / an `else` value can't be a C# expression.
        Zig.WhileElseExpr or Zig.ForElseExpr or Zig.LabeledWhileElseExpr or Zig.LabeledForElseExpr
            or Zig.WhileElseReturnExpr or Zig.ForElseReturnExpr or Zig.ForRefElseExpr or Zig.ForRefElseReturnExpr
            or Zig.WhileContAssignElseExpr => true,
        _ => false,
    };

    /// <summary>True when any prong of a switch EXPRESSION needs statements to yield its value — a
    /// block-bodied (<c>=&gt; { … }</c>) or capturing (<c>=&gt; |x| { … }</c>) prong, or a bare-expr
    /// prong whose value is itself a labeled value-block (<c>=&gt; blk: { … break :blk v; }</c>), or a <c>|v| expr</c>
    /// capture prong over a runtime <paramref name="subjectItem"/> (a union's payload).</summary>
    private static bool SwitchExprNeedsStmt(Item prongsItem, Item subjectItem) =>
        Flatten(prongsItem).Any(p => p.Content switch
        {
            Zig.Prong or Zig.ProngCapture or Zig.ProngCaptureRef => true,
            Zig.ProngJump or Zig.ProngCaptureJump => true,   // a `break` / `continue` arm is a statement
            // `.comptime_int => comptime { …; return result; }` (std.math.log2): the block returns from the function.
            Zig.ProngComptimeBlock => true,
            // `else => return error.InvalidCharacter` (std.fmt.charToDigit): a returning arm is a statement too.
            Zig.ProngReturn or Zig.ProngReturnVoid or Zig.ProngCaptureReturn or Zig.ProngCaptureReturnVoid => true,
            Zig.ProngExpr pe => IsLabeledValue(pe.Arg2),
            // `.number => |v| v * 4` over a union: the capture binds a payload, which needs a statement. Over a
            // comptime `@typeInfo(T)` the capture is folded where the expression lowers, so it stays one.
            Zig.ProngCaptureExpr or Zig.ProngCaptureRefExpr or Zig.ProngCaptureTagExpr
                => subjectItem.Content is not Zig.BuiltinCall { Arg0: var sb } || Tok(sb) != "@typeInfo",
            _ => false,
        });

    /// <summary>Lower a value-position control-flow form that needs statements to produce its value
    /// (see <see cref="IsValueControlFlowStmt"/>) as a C# STATEMENT, then hand the result temp to
    /// <paramref name="consume"/> (the decl / return / assignment that reads it). Dispatches an
    /// <c>if</c>/<c>switch</c> branch-temp-fill (Milestone Y, part 1) and a <c>while/for … else</c>
    /// value loop (part 2) to their builders.</summary>
    private CStmt LowerValueControlFlowStmt(Item rhs, CType? sink, Func<Symbol, CStmt> consume) => rhs.Content switch
    {
        Zig.IfExpr or Zig.SwitchExpr or Zig.SwitchExprTrailing => LowerValueIfSwitch(rhs, sink, consume),
        Zig.ComptimeSwitchExpr c => LowerValueControlFlowStmt(c.Arg1, sink, consume),
        Zig.ComptimeIfExpr c     => LowerValueControlFlowStmt(c.Arg1, sink, consume),
        Zig.WhileElseExpr or Zig.ForElseExpr or Zig.LabeledWhileElseExpr or Zig.LabeledForElseExpr
            or Zig.WhileElseReturnExpr or Zig.ForElseReturnExpr or Zig.ForRefElseExpr or Zig.ForRefElseReturnExpr
            or Zig.WhileContAssignElseExpr
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
            Zig.IfExpr e when TryFoldComptimeCondition(e.Arg2) is { } taken => FillValueTemp(taken ? e.Arg4 : e.Arg6, rt),
            Zig.IfExpr e => new If(LowerExpr(e.Arg2),
                                   new Block(new List<CStmt> { FillValueTemp(e.Arg4, rt) }),
                                   new Block(new List<CStmt> { FillValueTemp(e.Arg6, rt) })),
            Zig.SwitchExpr s         => BuildValueSwitch(s.Arg2, s.Arg5, rt),
            Zig.SwitchExprTrailing s => BuildValueSwitch(s.Arg2, s.Arg5, rt),
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
    /// the <c>else</c> value type. V1 cuts (deferred to the grammar): a for-RANGE / indexed / capture
    /// value loop.</summary>
    private CStmt LowerLoopValue(Item rhs, CType? sink, Func<Symbol, CStmt> consume)
    {
        string? label = null;
        Item condOrIter, blockItem, elseItem;
        string? elemName = null;
        var byRef = false;
        (Item Target, Item Op, Item Value)? contAssign = null;
        switch (rhs.Content)
        {
            case Zig.WhileElseExpr w:        condOrIter = w.Arg2; blockItem = w.Arg4; elseItem = w.Arg6; break;
            case Zig.ForElseExpr f:          condOrIter = f.Arg2; elemName = Tok(f.Arg5); blockItem = f.Arg7; elseItem = f.Arg9; break;
            case Zig.LabeledWhileElseExpr w: label = Tok(w.Arg0); condOrIter = w.Arg4; blockItem = w.Arg6; elseItem = w.Arg8; break;
            case Zig.LabeledForElseExpr f:   label = Tok(f.Arg0); condOrIter = f.Arg4; elemName = Tok(f.Arg7); blockItem = f.Arg9; elseItem = f.Arg11; break;
            case Zig.WhileElseReturnExpr w:  condOrIter = w.Arg2; blockItem = w.Arg4; elseItem = w.Arg6; break;
            case Zig.WhileContAssignElseExpr w:
                condOrIter = w.Arg2; contAssign = (w.Arg6, w.Arg7, w.Arg8); blockItem = w.Arg10; elseItem = w.Arg12; break;
            case Zig.ForElseReturnExpr f:    condOrIter = f.Arg2; elemName = Tok(f.Arg5); blockItem = f.Arg7; elseItem = f.Arg9; break;
            case Zig.ForRefElseExpr f:       condOrIter = f.Arg2; elemName = Tok(f.Arg6); blockItem = f.Arg8; elseItem = f.Arg10; byRef = true; break;
            case Zig.ForRefElseReturnExpr f: condOrIter = f.Arg2; elemName = Tok(f.Arg6); blockItem = f.Arg8; elseItem = f.Arg10; byRef = true; break;
            default: throw new IrUnsupportedException("internal: loop-value on " + (rhs.Content?.GetType().Name ?? "null"));
        }

        var n = _loopValueCounter++;
        var endLabel = "__lv" + n + "_end";
        var temp = _symbols.Declare(new Symbol { Name = "__lv" + n, Kind = SymKind.Var, Type = sink ?? CType.Int });
        var target = new LoopValueTarget { Temp = temp, EndLabel = endLabel, Label = label, Sink = sink, ResultType = sink };

        // Lower the loop with the value target active so a `break v` inside resolves to it. The cond /
        // iterable is lowered before the body (it can't `break`), so it never references the temp.
        _loopValues.Push(target);
        // A `for` names its element capture; a `while` has none.
        CStmt loop = elemName is { } elem
            ? LowerForSlice(LowerExpr(condOrIter), elem, null, blockItem, byRef)
            // `while (c) : (i += 1)` → the C `For` with that post, so a `continue` runs it (as the statement form).
            : contAssign is { } cont
                ? new For(null, LowerExpr(condOrIter), ContAssignPost(cont.Target, cont.Op, cont.Value), LowerBlock(blockItem))
                : new While(LowerExpr(condOrIter), LowerBlock(blockItem));
        _loopValues.Pop();

        // `… else return v`: normal completion RETURNS from the function, so the loop's value is its `break`s'
        // alone, and the code after the loop is reached only through the end label.
        if (elseItem.Content is Zig.ReturnArm returnArm)
        {
            var breakType = target.ResultType
                ?? throw new IrUnsupportedException("a value-position loop whose `else` returns must yield its value with `break v`");
            temp.Type = breakType;
            return new Seq(new List<CStmt>
            {
                new DeclStmt(new List<LocalDecl> { new(temp, new DefaultLit { Type = breakType }) }),
                loop,
                Hoisted(() => LowerReturn(returnArm.Arg1)),
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
        if (subject is not VarRef && prongs.Any(p => p.Content is Zig.ProngCaptureExpr or Zig.ProngCaptureReturn
                                                              or Zig.ProngCaptureReturnVoid or Zig.ProngCaptureJump))
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
            var (caseVals, capture, body) = prongItem.Content switch
            {
                Zig.ProngExpr pe                => (pe.Arg0, (string?)null, (Func<CStmt>)(() => FillValueTemp(pe.Arg2, rt))),
                Zig.ProngReturn pr              => (pr.Arg0, null, () => LowerReturn(pr.Arg3)),
                Zig.ProngReturnVoid pv          => (pv.Arg0, null, () => LowerReturnVoid()),
                Zig.ProngCaptureExpr ce         => (ce.Arg0, Tok(ce.Arg3), () => FillValueTemp(ce.Arg5, rt)),
                Zig.ProngCaptureReturn cr       => (cr.Arg0, Tok(cr.Arg3), () => LowerReturn(cr.Arg6)),
                Zig.ProngCaptureReturnVoid cv   => (cv.Arg0, Tok(cv.Arg3), () => LowerReturnVoid()),
                Zig.ProngJump pj                => (pj.Arg0, null, () => LowerProngJump(pj.Arg2)),
                Zig.ProngCaptureJump cj         => (cj.Arg0, Tok(cj.Arg3), () => LowerProngJump(cj.Arg5)),
                // A block that always jumps (`error.OutOfMemory => { return; }` in std.PriorityQueue's `ensureTotalCapacity`,
                // task #103) is `noreturn`, which a value switch accepts like `=> return`.
                Zig.Prong pb                    => (pb.Arg0, null, () => LowerNoReturnProngBlock(pb.Arg2)),
                Zig.ProngCapture pcb            => (pcb.Arg0, Tok(pcb.Arg3), () => LowerNoReturnProngBlock(pcb.Arg5)),
                _ => throw new IrUnsupportedException(
                    "a value-position switch prong must yield a value (`v => expr` or `v => blk: {… break :blk v;}`) "
                    + "or jump (`v => return …`); a void block prong or a `|*x|` capture in a switch expression is not supported yet"),
            };
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

    private CStmt LowerReturn(Item valueItem)
    {
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
    /// [bind(payload)] }</c>.</summary>
    private CStmt LowerControlFlowFallback(Item lhsItem, bool isCatch, string? capture, Item arm, Func<CExpr, CStmt>? bind)
    {
        var lhs = LowerExpr(lhsItem);
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
        return buf;
    }

    /// <summary>True when an item is an <c>error.Foo</c> literal, yielding the error name.</summary>
    private static bool IsErrorLit(Item it, out string name)
    {
        if (it.Content is Zig.ErrorLit e) { name = Tok(e.Arg2); return true; }
        name = "";
        return false;
    }

    /// <summary>True when an item is a set-qualified error reference <c>E.member</c> (Milestone X,
    /// part 2) — a <see cref="Zig.Field"/> whose base names a registered <c>error{…}</c> set —
    /// yielding the member name. dotcc erases set membership, so <c>E.member</c> resolves to the same
    /// flat code as the bare <c>error.member</c> (real zig: the same global error value). Recognized
    /// wherever <see cref="IsErrorLit"/> is — the value path and the <see cref="LowerReturn"/> error
    /// return. Instance (not static like <see cref="IsErrorLit"/>) because it reads <c>_errorSets</c>.</summary>
    private bool TryErrorSetMember(Item it, out string set, out string name)
    {
        if (it.Content is Zig.Field f && f.Arg0.Content is Zig.Ident id && _errorSets.Contains(Tok(id.Arg0)))
        {
            set = Tok(id.Arg0);
            name = Tok(f.Arg2);
            return true;
        }
        set = "";
        name = "";
        return false;
    }

    /// <summary>Reject a set-qualified <c>E.member</c> whose member is not declared in set <c>E</c>
    /// (Milestone X, part 3) — an illegal program real zig rejects, so dotcc does too (a good compiler
    /// rejects illegal programs). Lenient only if <c>E</c> somehow has no recorded members.</summary>
    private void ValidateSetMember(string set, string member)
    {
        if (_errorSetMembers.TryGetValue(set, out var members) && !members.Contains(member))
        {
            throw new CompileException($"zig: error '{member}' is not a member of error set '{set}'");
        }
    }

    /// <summary>Reject a directly-returned error (<c>return error.X;</c> / <c>return E.X;</c>) whose
    /// name is outside the current function's DECLARED error set (Milestone X, part 3) — e.g.
    /// <c>fn f() error{A}!u8 { return error.B; }</c>. No-op when the function is unconstrained (an
    /// inferred <c>!T</c> / <c>anyerror!T</c>, <see cref="_currentFnErrorSet"/> null). V1 checks the
    /// direct-return forms only; an error that flows in through a CALL or <c>try</c> is not yet
    /// set-checked (a documented cut — it would need cross-function set inference).</summary>
    private void CheckReturnedErrorInSet(string errName)
    {
        if (_currentFnErrorSet is { } cs && !cs.members.Contains(errName))
        {
            var which = cs.name is { } n ? $"error set '{n}'" : "the function's declared error set";
            throw new CompileException(
                $"zig: error '{errName}' is not a member of {which} (the function's return-error set)");
        }
    }

    /// <summary>A function's DECLARED error set, for the foreign-error return check (Milestone X,
    /// part 3). Returns false (UNCONSTRAINED — any error is accepted) for an inferred bare <c>!T</c>,
    /// for <c>anyerror!T</c>, or for an unknown set name (real zig infers / widens those); returns true
    /// with the allowed member names for an <c>E!T</c> over a declared set or an inline
    /// <c>error{…}!T</c>.</summary>
    private bool TryDeclaredErrorSet(Item retType, bool errUnion, out string? setName, out HashSet<string> members)
    {
        setName = null;
        members = new HashSet<string>(System.StringComparer.Ordinal);
        if (errUnion) { return false; }                          // bare `!T` — inferred set
        if (retType.Content is not Zig.ErrUnion eu) { return false; }
        switch (eu.Arg0.Content)
        {
            case Zig.Ident id when Tok(id.Arg0) != "anyerror" && _errorSetMembers.TryGetValue(Tok(id.Arg0), out var declared):
                setName = Tok(id.Arg0);
                members = declared;
                return true;
            case Zig.ErrorSet inlineSet:
                foreach (var m in WalkErrSetMembers(inlineSet.Arg2)) { members.Add(m); }
                return true;
            case Zig.ErrorSetEmpty:
                return true;                                     // `error{}!T` — never errors
            default:
                return false;                                    // anyerror / an unknown set name
        }
    }

    /// <summary>Lower a bare <c>error.Foo</c> value to its stable code in the flat global error set,
    /// typed <see cref="CType.ErrorSet"/> (rendered <c>ushort</c>). The code IS the value, so
    /// error-value equality compares codes (<c>e == error.Foo</c> → <c>e == &lt;code&gt;</c>). Shared
    /// by the bare-value lowering (here) and the captured-error binding; <c>return error.Foo;</c>
    /// keeps its dedicated <see cref="ErrUnionErr"/> / <see cref="ZigErrorThrow"/> path in
    /// <see cref="LowerReturn"/>.</summary>
    private CExpr LowerErrorLit(string name)
    {
        var code = ErrorCode(name);
        return new LitInt(code.ToString(CultureInfo.InvariantCulture), code) { Type = CType.ErrorSet };
    }

}
