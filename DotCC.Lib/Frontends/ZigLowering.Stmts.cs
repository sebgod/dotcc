#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Statements: <c>LowerBlock</c> / <c>LowerStmt</c> (the dispatch over every statement form, with
/// defer/errdefer), assignment (plain, compound, through a pointer, into an array, the const and narrowing checks) and
/// the plain <c>if</c> statement. The larger statement concerns live beside it: local declarations (LocalDecls), loops
/// (Loops), labeled blocks (Labeled), switches (Switch), captures (Captures), comptime statements (ComptimeExec) and
/// conditions (Conditions), control flow as a value (ValueFlow), returns and catch / orelse fallbacks (Returns), the ANF
/// statement hoist (Hoist) and error sets (ErrorSets). One concern of the <see cref="ZigLowering"/> binder; class doc +
/// shared state live in the main file.</summary>
internal sealed partial class ZigLowering
{

    // ---- statements ------------------------------------------------------

    private Block LowerBlock(Item block)
    {
        var items = new List<Item>();
        switch (block.Content)
        {
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
            case Zig.ConstDeclMods d:      return DeclOrComptime(d.Arg1, null, d.Arg4);  // const IDENT DeclMods = RhsExpr ;
            case Zig.VarDeclMods d:        return DeclOf(d.Arg1, null, d.Arg4);
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
            case Zig.StmtExpr { Arg0.Content: Zig.BuiltinCall { Arg2: not null } q } when Tok(q.Arg0) == "@setEvalBranchQuota":
                SetEvalBranchQuota(Flatten(q.Arg2));
                return new Seq(new List<CStmt>());
            // `@branchHint(.cold);` (hash_map's grow path, std's error paths): a layout hint to zig's optimizer
            // that must be a block's first statement; dotcc has nothing to emit for it.
            case Zig.StmtExpr { Arg0.Content: Zig.BuiltinCall { Arg2: not null } branchHint } when Tok(branchHint.Arg0) == "@branchHint":
                return new Seq(new List<CStmt>());
            // `@setRuntimeSafety(false);` (std.math.divCeil) / `@setFloatMode(.optimized);`: zig's per-scope safety and
            // float-mode switches. dotcc's C# is unchecked arithmetic with IEEE floats either way, so nothing to emit.
            case Zig.StmtExpr { Arg0.Content: Zig.BuiltinCall { Arg2: not null } scopeMode } when Tok(scopeMode.Arg0) is "@setRuntimeSafety" or "@setFloatMode":
                return new Seq(new List<CStmt>());
            // `@disableInstrumentation();` / `@disableIntrinsics();` (std's panic and memcpy paths): hints to
            // zig's own codegen, with nothing for dotcc to emit.
            case Zig.StmtExpr { Arg0.Content: Zig.BuiltinCall { Arg2: null } hint }
                when Tok(hint.Arg0) is "@disableInstrumentation" or "@disableIntrinsics":
                return new Seq(new List<CStmt>());
            // `comptime assert(c);` (std.math.cast's `comptime assert(@typeInfo(T) == .int);`): the assertion is
            // checked NOW. False is zig's compile error; true emits nothing. An `assert` returns void, which
            // the deferred comptime fold cannot splice, so it is never deferred. A condition that does not
            // fold here is taken on trust (a leniency: zig would evaluate it).
            case Zig.StmtExpr { Arg0.Content: Zig.PreComptime { Arg1.Content: Zig.CallArgs { Arg2: not null } ac } }
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
            // `if (opt) |p| if (n > 0) { … };` (std's Io/Writer.zig): an `if` expression standing as a statement (P3a).
            case Zig.StmtExpr e when IsStatementIf(e.Arg0): return LowerStatementIf(e.Arg0);
            case Zig.EmptyStmt:         return new Seq(new List<CStmt>());   // the `;` after a block-armed `if` expression statement
            case Zig.StmtExpr e:        return Hoisted(() => new ExprStmt(LowerExpr(e.Arg0)));

            // `x = value;`  → an assignment used as a statement. `_ = value;` is Zig's
            // explicit DISCARD (it forbids ignoring a non-void result) — lower it to a
            // bare expression statement, evaluated for its side effects.
            // A `catch`/`orelse` in the RHS (or a discarded `_ = f(a catch b())`) may hoist (ANF), so
            // lower the assignment under a hoist buffer.
            // `x op= y`, every operator (see LowerAssignOpStmt).
            case Zig.StmtAssign a:
                return LowerAssignOpStmt(a.Arg0, a.Arg1, a.Arg2);


            // if (cond) then [else else]  — `then`/`else`/`body` are themselves Stmts
            // (a single statement or a brace Block), which LowerStmt handles uniformly.
            case Zig.StmtIf f:          return LowerIfStmt(f.Arg2, f.Arg4, null);
            case Zig.StmtIfElse f:      return LowerIfStmt(f.Arg2, f.Arg4, f.Arg6);
            case Zig.StmtComptimeIf f:     return LowerComptimeIfStmt(f.Arg3, f.Arg5, null);
            case Zig.StmtComptimeIfElse f: return LowerComptimeIfStmt(f.Arg3, f.Arg5, f.Arg7);

            // `if (opt) |x| then [else else]` — payload-capturing `if` (Milestone M). Binds the
            // optional's payload (value `?T` or niche pointer) — or, with `else |e|`, an
            // error-union's success/error (part 3) — in the matching branch. See LowerIfCapture.
            case Zig.StmtIfCapture f:        return LowerIfCapture(f.Arg2, Tok(f.Arg5), f.Arg7, null, null);
            case Zig.StmtIfCaptureElse f:    return LowerIfCapture(f.Arg2, Tok(f.Arg5), f.Arg7, f.Arg9, null);
            case Zig.StmtIfCaptureRef f:     return LowerIfCapture(f.Arg2, Tok(f.Arg6), f.Arg8, null, null, byRef: true);
            case Zig.StmtIfCaptureRefElse f: return LowerIfCapture(f.Arg2, Tok(f.Arg6), f.Arg8, f.Arg10, null, byRef: true);
            case Zig.StmtIfCaptureErrElse f: return LowerIfCapture(f.Arg2, Tok(f.Arg5), f.Arg7, f.Arg12, Tok(f.Arg10));
            case Zig.StmtIfElseErr f:        return LowerIfCapture(f.Arg2, "_", f.Arg4, f.Arg9, Tok(f.Arg7));   // if (eu) S else |e| S
            // `if (c) return x else …;` — a `return Expr` then-arm (ReturnArm), otherwise the same `if`.
            case Zig.StmtIfReturnElse f:           return LowerIfStmt(f.Arg2, f.Arg4, f.Arg6);
            case Zig.StmtIfCaptureReturnElse f:    return LowerIfCapture(f.Arg2, Tok(f.Arg5), f.Arg7, f.Arg9, null);
            case Zig.StmtIfCaptureReturnErrElse f: return LowerIfCapture(f.Arg2, Tok(f.Arg5), f.Arg7, f.Arg12, Tok(f.Arg10));
            // An expression then-arm of a statement `if` (`if (c) f() else …`, `if (x) |s| s.close(io) else |_| {}`).
            case Zig.ThenExpr t:        return LowerArmStmt(t.Arg0);
            // The jump body of a statement `if` / prong (`if (c) return v else …`, `=> if (x) |v| break`), zig-grammar-peg P1b.
            case Zig.ReturnExpr or Zig.FbBreak or Zig.FbBreakLabel or Zig.FbBreakLabelValue or Zig.FbBreakValue
              or Zig.FbContinue or Zig.FbContinueLabel or Zig.FbContinueLabelValue:
                return LowerExitArm(stmt);
            case Zig.StmtIfAssignElse f:           return LowerIfStmt(f.Arg2, f.Arg4, f.Arg6);
            case Zig.AssignArm a:                  return LowerAssignOpStmt(a.Arg0, a.Arg1, a.Arg2);
            case Zig.StmtWhile w:       return new While(LowerExpr(w.Arg2), LowerStmt(w.Arg4));
            // `while (c) body else elsebody` (task #130): the else runs when the condition ends the loop, not a `break`.
            case Zig.StmtWhileElse w:   return LowerWhileElseStmt(w.Arg2, w.Arg4, w.Arg6);

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
                    System.Array.Empty<TypeSeed>(), System.Array.Empty<ValueSeed>(),
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
            case Zig.StmtContinueLabelValue cv: return LowerSwitchContinue(Tok(cv.Arg2), cv.Arg3);

            // `lbl: { … break :lbl; … }` — a labeled block STATEMENT (task #130): a void block `break :lbl;` leaves.
            case Zig.LabeledBlockStmt lbs: return LowerLabeledBlockStmt(lbs.Arg0);

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

            // `switch (subject) { prongs ,? } ;?` → the C IR Switch (subject=Arg2, prongs=Arg5; the optional
            // trailing comma and semicolon come after both). A tagged-union subject takes the capture path.
            case Zig.StmtSwitch s:         return LowerSwitchStmt(s.Arg2, s.Arg5);

            // `for (start..end) |i| body` → C `for (usize i = start; i < end; i++) body`. The
            // capture `i` is the usize loop index (its own scope so it doesn't leak); the end
            // is cast to usize so the comparison is unsigned-clean (C# forbids ulong<>signed).
            case Zig.StmtForRange f:
            {
                using var symbolScope = EnterSymbolScope();
                var start = LowerExpr(f.Arg2);
                var end = LowerExpr(f.Arg4);
                var iSym = _symbols.Declare(new Symbol { Name = Tok(f.Arg7), Kind = SymKind.Var, Type = CType.ULong });
                RecordValueBits(iSym, 64, null);   // a usize (task #132: real std's `{d}` asks its width)
                var iRef = new VarRef(iSym) { Type = CType.ULong, IsLValue = true };
                var init = new DeclStmt(new List<LocalDecl> { new(iSym, start) });
                var cond = new Binary(BinOp.Lt, iRef, new Cast(CType.ULong, end) { Type = CType.ULong }) { Type = CType.Int };
                var post = new Unary(UnOp.PostInc, iRef) { Type = CType.ULong };
                var body = LowerStmt(f.Arg9);
                symbolScope.Dispose();
                return new For(init, cond, post, body);
            }

            // A plain `for` over a comptime member list in a COMPTIME context (std.meta.stringToEnum's `comptime build_kvs:
            // { for (@typeInfo(T).@"enum".field_names, 0..) |name, i| kvs_array[i] = .{ name, @field(T, name) }; … }`, task
            // #116): zig runs the loop at compile time, so each capture is comptime-known, which is what `inline for`'s unroll
            // gives; a runtime loop would have nothing to iterate.
            case Zig.StmtForSlice ctf when _comptimeDepth > 0 && TryComptimeIterable(ctf.Arg2, out var ctList):
                return UnrollComptimeFor(new[] { (ctList, Tok(ctf.Arg5)) }, ctf.Arg7);
            case Zig.StmtForMulti ctm when _comptimeDepth > 0 && FirstForObject(ctm.Arg2) is { } ctFirst && TryComptimeIterable(ctFirst, out _):
                return UnrollComptimeMultiFor(ctm.Arg2, ctm.Arg6, ctm.Arg8);

            // `for (s) |x| body` — iterate a slice's elements (x = a per-iteration copy).
            case Zig.StmtForSlice f:     // for '(' Expr ')' '|' IDENT '|' Stmt
                return LowerForSlice(LowerExpr(f.Arg2), Tok(f.Arg5), null, f.Arg7, byRef: false, DeclaredElemBitsOfValue(f.Arg2));
            // `for (s) |*x| body` — BY-REFERENCE element capture: x is a `*T` into the slice (Milestone M, part 4).
            case Zig.StmtForSliceRef f:  // for '(' Expr ')' '|' '*' IDENT '|' Stmt
                return LowerForSlice(LowerExpr(f.Arg2), Tok(f.Arg6), null, f.Arg8, byRef: true);
            // The MULTI-object `for (a, b, 0.., …) |x, *y, i, …|` (task #108, one production since the fixed pair / triple /
            // indexed shapes): `(s, N..)` walks the slice with its index; any other shape walks every object in lockstep. The
            // `inline for` over comptime member lists takes these before they get here (LowerInlineLoop).
            case Zig.StmtForMulti f:      return LowerForMulti(f.Arg2, f.Arg6, f.Arg8);   // `,?` after the objects is Arg3
            // `for (…) |…| body else elsebody` (task #108): the else runs when the loop ends without a `break`.
            case Zig.StmtForSliceElse f:
                return LowerForParallel(new[] { new ForObject(f.Arg2, false, null) }, new[] { (Tok(f.Arg5), false) }, f.Arg7, f.Arg9);
            case Zig.StmtForSliceRefElse f:   // for (s) |*x| body else elsebody
                return LowerForParallel(new[] { new ForObject(f.Arg2, false, null) }, new[] { (Tok(f.Arg6), true) }, f.Arg8, f.Arg10);
            case Zig.StmtForRangeElse f:   // for (start..end) |i| body else elsebody
                return LowerForParallel(new[] { new ForObject(f.Arg2, true, f.Arg4) }, new[] { (Tok(f.Arg7), false) }, f.Arg9, f.Arg11);
            case Zig.StmtForMultiElse f:
            {
                var (objects, captures) = DecomposeForMulti(f.Arg2, f.Arg6);
                return LowerForParallel(objects, captures, f.Arg8, f.Arg10);
            }

            // A brace block in statement position (`Stmt -> Block`, pass-through).
            case Zig.Block:             return LowerBlock(stmt);

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
    /// <remarks>A conditional selects one of two arrays (an optional array's <c>x orelse fallback</c>, task #151): binding it
    /// without a copy would alias whichever it picked, so it counts too (a fresh arm is merely copied once more). So does
    /// the array a pointer names (`var c = p.*;`, task #184), whose C# rep is the pointer itself.</remarks>
    private static bool IsArrayLvalue(CExpr e) =>
        e is VarRef or Member or DotCC.Ir.Index or CondExpr or Unary { Op: UnOp.Deref } || e is Paren p && IsArrayLvalue(p.Inner);
    /// <summary>Declare <paramref name="sym"/> as a fresh <c>[N]T</c> local and copy <paramref name="source"/>'s elements
    /// into it.</summary>
    private static CStmt ArrayValueCopyDecl(Symbol sym, CType.Array arr, long count, CExpr source)
    {
        // A multi-dimensional array (`const plane = cube[1];` of a `[2][2][2]u8`, task #152) is one flat run: its storage
        // and its copy count the innermost elements.
        var flatCount = count * RowFlatCount(arr);
        var countLit = new LitInt(flatCount.ToString(CultureInfo.InvariantCulture), flatCount) { Type = CType.Int };
        var target = new VarRef(sym) { Type = arr, IsLValue = true };
        return new Seq(new List<CStmt>
        {
            new ArrayDecl(sym, arr.FlatElement, countLit, null),
            new ExprStmt(ArrayElementCopy(target, source, arr, count)),
        });
    }
    /// <summary>The innermost elements per outer element of <paramref name="arr"/>: 1 for a plain array, the row's flat
    /// count for a multi-dimensional one.</summary>
    private static long RowFlatCount(CType.Array arr) => arr.Element.Unqualified is CType.Array row ? FlatElementCount(row) : 1;
    /// <summary><paramref name="count"/> elements of <paramref name="source"/> copied into <paramref name="target"/>
    /// (both arrays, rendered as their element pointers; a multi-dimensional one copies its flat run).</summary>
    private static CExpr ArrayElementCopy(CExpr target, CExpr source, CType.Array arr, long count)
    {
        var elem = arr.FlatElement.Unqualified;
        count *= RowFlatCount(arr);
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
        // `v[i] +%= x` on a SIMD vector lane (task #155) replaces the lane.
        if (TryVectorLaneStore(targetItem, op, valueItem) is { } laneStore) { return laneStore; }
        var target = LowerExpr(targetItem);
        // A shift's count is not the target's type (std.math.gcd's `x >>= @intCast(xz)`, task #86): a cast builtin there takes
        // C#'s `int` shift count.
        var value = op is BinOp.Shl or BinOp.Shr && valueItem.Content is Zig.BuiltinCall { Arg2: not null, Arg0: var shiftCast }
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
        // The same when a LATER operand settles it (std.mem.reverse's `use_vectors and !@inComptime() and @bitSizeOf(T) > 0
        // and std.math.isPowerOfTwo(@bitSizeOf(T))` over a 24-byte struct, task #108): the dead arm is not lowered, and the
        // condition still runs for its runtime operands.
        if ((_inlineUnrollDepth > 0 || _inGenericInstance) && (SettlesShortCircuit(condItem, false) || SettlesShortCircuit(condItem, true)))
        {
            var always = SettlesShortCircuit(condItem, true);
            var arm = always ? LowerStmt(thenItem) : elseItem is { } laterElse ? LowerStmt(laterElse) : new Seq(new List<CStmt>());
            return new Seq(new List<CStmt> { new ExprStmt(LowerExpr(condItem)), arm });
        }
        var cond = LowerExpr(condItem);
        if (_inGenericInstance && _ir.ConstEval(cond) is { } cv)
        {
            if (cv != 0) { return LowerStmt(thenItem); }
            return elseItem is { } taken ? LowerStmt(taken) : new Seq(new List<CStmt>());
        }
        return new If(cond, LowerStmt(thenItem), elseItem is { } el ? LowerStmt(el) : null);
    }
    /// <summary>Dispatch a <c>switch</c> statement: lower the subject once, then route a
    /// tagged-union subject (a value or pointer-to a registered <c>union(enum)</c>) to
    /// <see cref="LowerUnionSwitch"/> (the tag-discriminant + payload-capture path) and any other
    /// subject to the plain <see cref="LowerSwitch"/>.</summary>
    /// <summary>Each container const whose address was taken, by (container, name): its static global.</summary>
    private readonly Dictionary<(string Container, string Name), Symbol> _staticContainerConsts = new();
    /// <summary>Container consts whose labeled block evaluated to a <c>comptime_int</c>, kept as that literal rather than a
    /// static (task #108), by container and name.</summary>
    private readonly Dictionary<(string Container, string Name), CExpr> _foldedContainerConsts = new();
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
            // An ARRAY const of literal elements too (std.hash.XxHash3's `const secret = &default_secret;` over a
            // `[192]u8`, task #178): inlined at each read, `&` of it had been a temp's address (`byte**`, a bad emit).
            if (value.Type.Unqualified is not (CType.Named or CType.Array) || !IsStaticInitializer(value)) { return null; }
            sym = _symbols.Declare(new Symbol
            {
                Name = $"{container}__{name}__static", Kind = SymKind.Var, Type = value.Type, Storage = Storage.Static, IsGlobal = true,
            });
            _ir.Globals.Add(new GlobalVar(sym, value));
            sym.AddressTaken = true;
            // The interpreter reads a static array through its value, so a comptime block over `&empty_vals` still evaluates.
            if (value.Type.Unqualified is CType.Array && _ir.EvalComptimeValue(value) is { } arrayValue) { _ir.ComptimeGlobals[sym] = arrayValue; }
            _staticContainerConsts[(container, name)] = sym;
        }
        // A static array renders as its element pointer, which is already the pointer to the array (as `&arr` of a local is).
        if (sym.Type.Unqualified is CType.Array)
        {
            return new VarRef(sym) { Type = new CType.Pointer(sym.Type.WithQuals(TypeQual.Const)) };
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
        // Inside a block the comptime interpreter evaluates (TryComptimeReturnBlock, task #100) the literal is comptime
        // memory, whatever its fields: the interpreter reads `&` of an aggregate as the aggregate, and the splice pins it.
        if (_loweringForComptimeEval > 0) { return new Unary(UnOp.AddrOf, value) { Type = new CType.Pointer(pointee) }; }
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
        StackArray sa => sa.Elems.All(IsStaticInitializer),
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
    /// <summary>An assignment as a statement, whichever form spells it (a statement, an <c>if</c> arm, a switch prong body),
    /// by its <c>AssignOp</c>: <c>=</c> a plain store (<c>_ = v</c> a discard, see <see cref="LowerAssignStmt"/>); a compound
    /// or wrapping operator the shared Assign node with its <see cref="BinOp"/> as CompoundOp (a native C# <c>op=</c>, which
    /// evaluates the lvalue once and already wraps in the unchecked context); a saturating one
    /// <c>x = ZigMath.Sat…(x, y)</c>.</summary>
    private CStmt LowerAssignOpStmt(Item lhs, Item opItem, Item rhs) => opItem.Content switch
    {
        Zig.AopAssign => LowerAssignStmt(lhs, rhs),
        Zig.AopAddSat => SatCompoundAssign(lhs, "SatAdd", rhs),
        Zig.AopSubSat => SatCompoundAssign(lhs, "SatSub", rhs),
        Zig.AopMulSat => SatCompoundAssign(lhs, "SatMul", rhs),
        _ when CompoundOpOf(opItem) is { } op => CompoundAssign(lhs, op, rhs),
        _ => throw new IrUnsupportedException("zig: assignment operator " + (opItem.Content?.GetType().Name ?? "null")),
    };
    /// <summary>The locals declared with a type annotation (<c>var a: u8 = 0;</c>): their zig type is the spelled one, so
    /// a store into one is a certain integer sink (task #167). An inferred local's lowered type may be C's, not zig's.</summary>
    private readonly HashSet<Symbol> _annotatedLocals = new();
    /// <summary>Reject a plain assignment that narrows a runtime integer (task #167): <c>a = x;</c> with <c>a: u8</c> and
    /// <c>x: u16</c> is zig's "expected type 'u8', found 'u16'". The target's zig type must be certain: an annotated local,
    /// a struct field or an array element (their lowered types are the spelled ones, a generic's carrier at worst, which
    /// is never narrower).</summary>
    private void RejectNarrowingStore(Item lhsItem, Item rhsItem)
    {
        CExpr target;
        switch (lhsItem.Content)
        {
            case Zig.Ident id when _symbols.Resolve(Tok(id.Arg0)) is { } local && _annotatedLocals.Contains(local):
                RejectIntegerNarrowing(rhsItem, local.Type, _valueBits.TryGetValue(local, out var localBits) ? localBits : null);
                return;
            case Zig.Field or Zig.Index:
                using (EnterThrowawayHoist()) { target = LowerExpr(lhsItem); }
                break;
            default:
                return;
        }
        var targetBits = target is Member { Base.Type: var objType, Field: var field }
                         && (objType?.Unqualified is CType.Pointer { Pointee: var pointee } ? pointee.Unqualified : objType?.Unqualified) is CType.Named owner
                         && _structFieldBits.TryGetValue((owner.Name, field), out var fieldBits)
            ? fieldBits
            : (int?)null;
        RejectIntegerNarrowing(rhsItem, target.Type, targetBits);
    }
    private CStmt LowerAssignStmt(Item lhsItem, Item rhsItem)
        => TryAssignComptimeVar(lhsItem, null, rhsItem) ?? RejectConstStore(lhsItem) ?? Hoisted(() =>
        {
            RejectNarrowingStore(lhsItem, rhsItem);
            if (lhsItem.Content is Zig.Ident lhs && Tok(lhs.Arg0) == "_")
            {
                // `_ = attr;` / `_ = T;` over a comptime-only binding (a `field_attrs` entry or a type an `inline for` capture
                // bound, task #162): there is no runtime value to evaluate.
                if (rhsItem.Content is Zig.Ident discardedName && Tok(discardedName.Arg0) is var discardedText
                    && _symbols.Resolve(discardedText) is null
                    && (_comptimeAttrs.ContainsKey(discardedText) || _typeAliases.ContainsKey(discardedText)))
                {
                    return new Seq(new List<CStmt>());
                }
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
                // An anonymous list (`… [0..2].* = .{ high, low };` in std.unicode) is the array the slice views, so it lowers
                // at that array type when the view's length is known.
                var copyValue = rhsItem.Content is Zig.AnonStructInit && copyDest is SliceNew { Len: var viewLen }
                                && _ir.ConstEval(viewLen) is { } viewCount and >= 0 and <= int.MaxValue
                    ? LowerExprSink(rhsItem, new CType.Array(copyElem, (int)viewCount))
                    : LowerExpr(rhsItem);
                // A `@Vector` stored into the array the slice views (std.unicode.utf8ToUtf16LeImpl's
                // `utf16le[dest_index..][0..chunk_len].* = utf16_chunk;`, task #144) writes its lanes there.
                if (copyValue.Type.Unqualified is CType.Vector)
                {
                    var destPtr = new Member(copyDest, "Ptr", false) { Type = new CType.Pointer(copyElem) };
                    return new ExprStmt(new Call("ZigVec.Store", new List<CExpr> { copyValue, destPtr }) { Type = CType.Void });
                }
                var copySrcElem = SliceElementOf(copyValue).Unqualified;
                var copySrc = copyValue.Type.Unqualified is CType.Slice
                    ? copyValue
                    : CoerceToSlice(copyValue, new CType.Slice(copySrcElem.WithQuals(TypeQual.Const)));
                return new ExprStmt(new ZigMemCall("CopyForwards", copyElem, new List<CExpr> { copyDest, copySrc }) { Type = CType.Void });
            }
            // `buffer.* = @bitCast(value)` with `buffer: *[N]u8` (std.mem.writeInt): the value's BYTES stored into the array
            // the pointer names. (It was once a store to the pointer itself, `buffer = BitCast<ulong, byte*>(value)`.)
            if (lhsItem.Content is Zig.Deref { Arg0: var arrayPtrItem }
                && rhsItem.Content is Zig.BuiltinCall { Arg2: not null, Arg0: var bitCastTok } bitCast && Tok(bitCastTok) == "@bitCast"
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
            // `v[i] = x` on a SIMD vector lane (std.crypto.blake3's counterLow vector, task #155) replaces the lane.
            if (TryVectorLaneStore(lhsItem, null, rhsItem) is { } laneStore) { return new ExprStmt(laneStore); }
            var target = LowerExpr(lhsItem);
            // `d = a;` between arrays: an element copy (the C# rep is the element pointer, so a plain assignment
            // would alias the storage). `d = undefined;` changes nothing. A ROW of a multi-dimensional array
            // (`self.cv_stack[self.cv_stack_len] = new_cv;` in std.crypto.blake3, task #154) is such a target too, and so is
            // the array a `*[N]T` names (`out.* = @as(*[digest_length]u8, @ptrCast(&d.h)).*;` in std.crypto.blake2's final,
            // task #184): a plain assignment there rebound the pointer PARAMETER, and the caller's array was never written.
            if (target.Type.Unqualified is CType.Array { Count: { } assignCount } assignArr
                && target is VarRef or Member or DotCC.Ir.Index or Unary { Op: UnOp.Deref })
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
            // Storing a void value into void storage (`unit = {};`) moves no data; nor into void-as-data storage (the runtime
            // `Unit`), such as hash_map's `self.values()[idx] = value;` for a set (`V = void`, std.BufSet, task #198), where the
            // value is an erased `void` parameter.
            if (IsVoidValueType(target.Type) && IsErasableVoid(value)) { return new Seq(new List<CStmt>()); }
            return new ExprStmt(new Assign(null, target, value) { Type = target.Type });
        });
    /// <summary>True when a callee names <c>assert</c> (a bare alias, <c>const assert = std.debug.assert;</c>,
    /// or a dotted <c>std.debug.assert</c>).</summary>
    private static bool IsAssertCallee(Item callee) => callee.Content switch
    {
        Zig.Ident id => Tok(id.Arg0) == "assert",
        Zig.Field f => Tok(f.Arg2) == "assert",
        _ => false,
    };
}
