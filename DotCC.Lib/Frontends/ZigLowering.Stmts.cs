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
                case Zig.StmtErrdefer d: cleanupBody = d.Arg1; onErrorOnly = true;  break;
                default:
                    var lowered = LowerStmt(it);
                    stmts.Add(lowered);
                    // In a generic instance (wall-plan W3a), a statement that comptime-folded to an
                    // unconditional terminator (a taken `if (n < 2) return n;`) makes the REST of the
                    // block comptime-DEAD — stop, so a pruned branch's generic calls (the sibling
                    // `return fib(n-1)+fib(n-2)`) never instantiate. Sound in general (dead code after a
                    // proven terminator), gated to instance bodies so ordinary lowering is untouched.
                    if (_inGenericInstance && Terminates(lowered)) { return stmts; }
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
            // pass 0; a local one is first seen here mid-pass-2) and emits NO runtime statement. V1:
            // struct only (all three layouts); a local enum/union is a loud cut.
            case Zig.StructDecl s:       return LowerLocalStruct(Tok(s.Arg1), s.Arg5, AggregateLayout.Default);
            case Zig.StructDeclEmpty s:  return LowerLocalStruct(Tok(s.Arg1), null,   AggregateLayout.Default);
            case Zig.ExternStructDecl s: return LowerLocalStruct(Tok(s.Arg1), s.Arg6, AggregateLayout.Sequential);
            case Zig.PackedStructDecl s: return LowerLocalStruct(Tok(s.Arg1), s.Arg6, AggregateLayout.Packed);
            case Zig.EnumDecl or Zig.EnumDeclTyped or Zig.UnionDeclEnum or Zig.UnionDeclTagged or Zig.UnionDeclUntagged:
                throw new IrUnsupportedException(
                    "zig: an in-function `enum`/`union` declaration is not supported yet (wall-plan W2 is struct-only); declare it at top/container level");
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
            case Zig.StmtExpr e when IsControlFlowFallback(e.Arg0, out var cfL, out var cfC, out var cfR):
                return LowerControlFlowFallback(cfL, cfC, cfR, null);
            case Zig.StmtExpr e:        return Hoisted(() => new ExprStmt(LowerExpr(e.Arg0)));

            // `x = value;`  → an assignment used as a statement. `_ = value;` is Zig's
            // explicit DISCARD (it forbids ignoring a non-void result) — lower it to a
            // bare expression statement, evaluated for its side effects.
            // A `catch`/`orelse` in the RHS (or a discarded `_ = f(a catch b())`) may hoist (ANF), so
            // lower the assignment under a hoist buffer.
            case Zig.StmtAssign a:
                return Hoisted(() =>
                {
                    if (a.Arg0.Content is Zig.Ident lhs && Tok(lhs.Arg0) == "_")
                    {
                        return new ExprStmt(LowerExpr(a.Arg2));
                    }
                    var target = LowerExpr(a.Arg0);
                    // `x = blk: { … break :blk v; };` — a labeled value-block assignment (Milestone L,
                    // part 2): temp-fill against the lvalue's type, then assign the result temp into it.
                    if (a.Arg2.Content is Zig.LabeledBlock lb)
                    {
                        return LowerLabeledValueBlock(Tok(lb.Arg0), lb.Arg2, target.Type,
                            temp => new ExprStmt(new Assign(null, target, new VarRef(temp) { Type = temp.Type }) { Type = target.Type }));
                    }
                    // `x = switch (y) { … blk: {…} };` / `x = if (c) blk:{…} else …;` — a value-position
                    // if/switch with a statement-producing branch (Milestone Y, part 1): temp-fill against
                    // the lvalue's type, then assign the result temp into it.
                    if (IsValueControlFlowStmt(a.Arg2))
                    {
                        return LowerValueControlFlowStmt(a.Arg2, target.Type,
                            temp => new ExprStmt(new Assign(null, target, new VarRef(temp) { Type = temp.Type }) { Type = target.Type }));
                    }
                    var value = LowerExprSink(a.Arg2, target.Type);   // target type is the sink (`x = .member;`)
                    return new ExprStmt(new Assign(null, target, value) { Type = target.Type });
                });

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

            // `if (opt) |x| then [else else]` — payload-capturing `if` (Milestone M). Binds the
            // optional's payload (value `?T` or niche pointer) — or, with `else |e|`, an
            // error-union's success/error (part 3) — in the matching branch. See LowerIfCapture.
            case Zig.StmtIfCapture f:        return LowerIfCapture(f.Arg2, Tok(f.Arg5), f.Arg7, null, null);
            case Zig.StmtIfCaptureElse f:    return LowerIfCapture(f.Arg2, Tok(f.Arg5), f.Arg7, f.Arg9, null);
            case Zig.StmtIfCaptureErrElse f: return LowerIfCapture(f.Arg2, Tok(f.Arg5), f.Arg7, f.Arg12, Tok(f.Arg10));
            case Zig.StmtWhile w:       return new While(LowerExpr(w.Arg2), LowerStmt(w.Arg4));

            // `while (cond) : (cont) body` → the C IR `For` (no init): the cont runs after each
            // iteration AND on `continue`, exactly matching C's for-update — so `continue`
            // inside the loop runs the cont, faithful to Zig. The assignment cont (`i = i + 1`)
            // builds an Assign CExpr post (mirroring StmtAssign); the bare-expr cont a plain one.
            case Zig.StmtWhileCont w:
                return new For(null, LowerExpr(w.Arg2), LowerExpr(w.Arg6), LowerStmt(w.Arg8));
            case Zig.StmtWhileContAssign w:
            {
                var post = LowerExpr(w.Arg6);
                var postVal = LowerExpr(w.Arg8);
                var postAssign = new Assign(null, post, postVal) { Type = post.Type };
                return new For(null, LowerExpr(w.Arg2), postAssign, LowerStmt(w.Arg10));
            }

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
            // (like stmtWhileContAssign); the bare-expr form a plain one.
            case Zig.StmtWhileCaptureCont w:
                return LowerWhileCapture(w.Arg2, Tok(w.Arg5), w.Arg11, null, LowerExpr(w.Arg9));
            case Zig.StmtWhileCaptureContAssign w:
            {
                var cLhs = LowerExpr(w.Arg9);
                var cRhs = LowerExpr(w.Arg11);
                return LowerWhileCapture(w.Arg2, Tok(w.Arg5), w.Arg13, null,
                    new Assign(null, cLhs, cRhs) { Type = cLhs.Type });
            }

            // `break;` / `continue;` — reuse the C IR loop-control nodes (the C# backend
            // renders them verbatim; valid inside the while/for forms above).
            case Zig.StmtBreak:    return new Break();
            case Zig.StmtContinue: return new Continue();

            // `break v;` — an unlabeled value break (Milestone Y, part 2): yield `v` from the innermost
            // value-position loop (`while/for … else`). Assigns its result temp and jumps to its end
            // label (skipping the loop's `else`).
            case Zig.StmtBreakValue b: return LowerBreakValue(b.Arg1);

            // `break :blk v;` — yield a value from the enclosing labeled value-block (Milestone L,
            // part 2). Assigns the block's result temp and jumps to its end label (LowerLabeledBreak).
            case Zig.StmtBreakLabelValue b: return LowerLabeledBreak(Tok(b.Arg2), b.Arg3);

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
                return LowerForSlice(LowerExpr(f.Arg2), Tok(f.Arg5), null, f.Arg7, byRef: false);
            // `for (s) |*x| body` — BY-REFERENCE element capture: x is a `*T` into the slice (Milestone M, part 4).
            case Zig.StmtForSliceRef f:  // for '(' Expr ')' '|' '*' IDENT '|' Stmt
                return LowerForSlice(LowerExpr(f.Arg2), Tok(f.Arg6), null, f.Arg8, byRef: true);
            // `for (s, 0..) |x, i| body` — also bind the usize index (counter + start).
            case Zig.StmtForSliceIdx f:  // for '(' Expr ',' Expr '..' ')' '|' IDENT ',' IDENT '|' Stmt
                return LowerForSlice(LowerExpr(f.Arg2), Tok(f.Arg8), (Tok(f.Arg10), LowerExpr(f.Arg4)), f.Arg12, byRef: false);
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
    {
        var target = LowerExpr(targetItem);
        var value = LowerExprSink(valueItem, target.Type);
        return new ExprStmt(new Assign(op, target, value) { Type = target.Type });
    }

    /// <summary>Lower a local <c>const</c> declaration, intercepting a comptime allocator /
    /// namespace binding first (Milestone F): <c>const std = @import("std");</c> /
    /// <c>const a = std.heap.page_allocator;</c> carry no runtime value, so they register the
    /// alias (<see cref="TryComptimeConstBinding"/>) and emit nothing (an empty <see cref="Seq"/>).
    /// Any other <c>const</c> is an ordinary <see cref="DeclOf"/>.</summary>
    private CStmt DeclOrComptime(Item nameTok, Item? typeItem, Item initExpr)
        => TryComptimeConstBinding(Tok(nameTok), initExpr)
            ? new Seq(new List<CStmt>())
            : DeclOf(nameTok, typeItem, initExpr);

    // `const`/`var x = init;` — lower under an ANF hoist buffer so a catch/orelse in a SUB-expression
    // of the initializer (`const r = 1 + (a catch b());`) lifts to a temp before the decl. A
    // WHOLE-init catch / control-flow fallback is intercepted at the top of DeclOfInner (its own
    // statement lowering), leaving the buffer empty, so this wrap is a no-op for those.
    private CStmt DeclOf(Item nameTok, Item? typeItem, Item initExpr)
        => Hoisted(() => DeclOfInner(nameTok, typeItem, initExpr));

    private CStmt DeclOfInner(Item nameTok, Item? typeItem, Item initExpr)
    {
        // Compute the declared type FIRST: a result-located init (`.member` / `.{…}`) needs
        // it as its sink, so resolve the annotation before lowering the initializer.
        var declared = typeItem is not null ? LowerType(typeItem) : null;
        // `const x = blk: { … break :blk v; };` — a labeled value-block initializer. Temp-fill it
        // (the declared type, if any, is the sink), then bind `x` to the result temp.
        if (initExpr.Content is Zig.LabeledBlock lb)
        {
            return LowerLabeledValueBlock(Tok(lb.Arg0), lb.Arg2, declared, temp =>
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
        if (IsControlFlowFallback(initExpr, out var cfLhs, out var cfCatch, out var cfRet))
        {
            return LowerControlFlowFallback(cfLhs, cfCatch, cfRet, payload =>
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
            if (arrInit is ComptimeFold && !sentinel)
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
        var type = declared ?? init.Type ?? CType.Int;
        var sym2 = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = type });
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
    {
        var n = _blockLabelCounter++;
        var endLabel = "__blk" + n + "_end";
        // Declared with a provisional type; retyped below once the result type is resolved. The
        // counter-unique name never collides, so declaring it up front is safe.
        var temp = _symbols.Declare(new Symbol { Name = "__blk" + n, Kind = SymKind.Var, Type = sink ?? CType.Int });
        var target = new LabeledBlockTarget { Label = label, Temp = temp, EndLabel = endLabel, Sink = sink, ResultType = sink };
        _labeledBlocks.Push(target);
        var body = LowerBlock(blockItem);   // each `break :label v` reads `target` via LowerLabeledBreak
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
        if (_loopValues.Count == 0)
        {
            throw new IrUnsupportedException(
                "`break <value>;` is only valid inside a value-position `while`/`for … else` loop");
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
        var loop = LowerStmt(loopItem);   // a While / For / DoWhile; break/continue :lbl read `t`
        _labeledLoops.Pop();
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
                return UnrollInlineWhile(w.Arg2, w.Arg6, w.Arg8, w.Arg10);

            default:
                throw new IrUnsupportedException(
                    "`inline` is only supported on a counted `for (lo..hi) |i|` range loop, a "
                    + "`for (arr) |x|` over a fixed array, or an `inline while (c) : (i = …)` with a "
                    + "`comptime var` counter (comptime unrolling) — the indexed `|x, i|` / by-ref "
                    + "`|*x|` `for` forms, `inline for` over a slice, and a bare/expr-cont `inline "
                    + "while` are not supported yet");
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
    private CStmt UnrollInlineWhile(Item condItem, Item contLhsItem, Item contRhsItem, Item bodyItem)
    {
        // The continue-expr target must resolve (WITHOUT substitution) to a tracked comptime var.
        if (contLhsItem.Content is not Zig.Ident contId
            || _symbols.Resolve(Tok(contId.Arg0)) is not { } contSym
            || !_comptimeVars.ContainsKey(contSym))
        {
            throw new IrUnsupportedException(
                "`inline while` requires a `comptime var` loop counter advanced by the "
                + "continue-expression (`comptime var i = …; inline while (i < N) : (i = i + step) { … }`)");
        }

        var copies = new List<CStmt>();
        while (true)
        {
            if (_ir.ConstEval(LowerExpr(condItem)) is not { } cond)
            {
                throw new IrUnsupportedException("`inline while` condition must be compile-time-known");
            }
            if (cond == 0) { break; }
            if (copies.Count >= InlineUnrollCap)
            {
                throw new IrUnsupportedException(
                    $"`inline while` exceeded the unroll cap ({InlineUnrollCap}) — a non-terminating comptime condition?");
            }
            // Unroll one body copy (the comptime counter substitutes to its current value within it).
            _symbols.EnterScope();
            var body = LowerStmt(bodyItem);
            _symbols.ExitScope();
            if (HasLoopEscape(body))
            {
                throw new IrUnsupportedException(
                    "`break`/`continue` inside an `inline while` body is not supported yet (the loop is unrolled)");
            }
            copies.Add(body is Block ? body : new Block(new List<CStmt> { body }));
            // Advance the counter: fold the continue-expr RHS (with the current value), store it back.
            if (_ir.ConstEval(LowerExpr(contRhsItem)) is not { } next)
            {
                throw new IrUnsupportedException("`inline while` continue-expression must be compile-time-known");
            }
            _comptimeVars[contSym] = (next, _comptimeVars[contSym].Type);
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
        var initExpr = LowerExpr(initItem);
        var ctype = typeItem is { } ti ? LowerType(ti) : initExpr.Type;
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
        _symbols.EnterScope();
        ExecuteComptimeStmt(blockItem);
        _symbols.ExitScope();
        return new Seq(new List<CStmt>());   // compile-time-only — nothing runs at runtime
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
            default:
                throw new IrUnsupportedException(
                    $"comptime block: statement '{s.Content?.GetType().Name}' is not supported — only "
                    + "var/const decls, assignments to a comptime var, and `while` loops run at comptime");
        }
    }

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
    /// signed constant).</summary>
    private static CExpr ComptimeVarLit(long v, CType t)
    {
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
    /// table's CS0136 rename covers any leak regardless). A bare <c>break</c>/<c>continue</c> in the
    /// body is rejected — unrolling removes the loop, so it would have no target. The count is capped to
    /// bound emitted-code size.</summary>
    private CStmt UnrollInlineFor(long count, string captureName, CType captureType, Item bodyItem, System.Func<long, CExpr> initFor)
    {
        if (count > InlineUnrollCap)
        {
            throw new IrUnsupportedException(
                $"`inline for` would unroll {count} iterations, exceeding the cap ({InlineUnrollCap})");
        }
        var copies = new List<CStmt>((int)count);
        for (long k = 0; k < count; k++)
        {
            _symbols.EnterScope();
            var sym = _symbols.Declare(new Symbol { Name = captureName, Kind = SymKind.Var, Type = captureType });
            var decl = new DeclStmt(new List<LocalDecl> { new(sym, initFor(k)) });
            var body = LowerStmt(bodyItem);
            _symbols.ExitScope();
            if (HasLoopEscape(body))
            {
                throw new IrUnsupportedException(
                    "`break`/`continue` inside an `inline for` body is not supported yet (the loop is "
                    + "unrolled, so there is no enclosing loop to target)");
            }
            copies.Add(new Block(new List<CStmt> { decl, body }));
        }
        return new Seq(copies);
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
    /// COMPTIME-KNOWN condition — one <see cref="IrBuilder.ConstEval"/> folds, because a comptime
    /// parameter substitutes a literal (e.g. <c>n &lt; 2</c>) — is a Zig comptime-if: only the TAKEN
    /// branch is lowered, so the dead branch's generic calls never instantiate. Combined with the
    /// block-level dead-code stop (<see cref="LowerStmtsWithDefers"/>), that's what lets a recursive
    /// comptime generic (<c>fib</c>) prune its base case and terminate. A RUNTIME condition (ConstEval
    /// returns null), or any <c>if</c> outside an instance body, lowers to the ordinary two-armed
    /// <see cref="If"/> — the condition is lowered exactly once either way.</summary>
    private CStmt LowerIfStmt(Item condItem, Item thenItem, Item? elseItem)
    {
        var cond = LowerExpr(condItem);
        if (_inGenericInstance && _ir.ConstEval(cond) is { } cv)
        {
            if (cv != 0) { return LowerStmt(thenItem); }
            return elseItem is { } taken ? LowerStmt(taken) : new Seq(new List<CStmt>());
        }
        return new If(cond, LowerStmt(thenItem), elseItem is { } el ? LowerStmt(el) : null);
    }

    private CStmt LowerIfCapture(Item condItem, string capName, Item thenItem, Item? elseItem, string? errCapName)
    {
        var cond = LowerExpr(condItem);
        var ct = cond.Type.Unqualified;

        // Hoist a side-effecting condition to a single-eval temp (a bare var is already re-readable).
        var pre = new List<CStmt>();
        CExpr condRef;
        if (cond is VarRef)
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

    /// <summary>Lower an optional capture-<c>while</c> <c>while (opt) |x| body</c> (Milestone M, part
    /// 2). The condition is re-evaluated EACH iteration (it commonly advances an iterator), so it lives
    /// inside the loop body — a fresh <c>__cap</c> per turn. When it yields a payload, bind <c>x</c> and
    /// run the body; otherwise break. Desugars to
    /// <code>while (true) { var __cap = cond; if (has) { var x = payload; body } else break; }</code>
    /// which produces a real <see cref="While"/> node, so a labeled break/continue composes via the
    /// existing labeled-loop machinery. A value optional <c>?T</c> tests <c>__cap.HasValue</c> / binds
    /// <c>.Value</c>; a niche optional pointer tests non-null / binds the pointer itself. <c>_</c> tests
    /// without binding. An error-union or non-optional condition is a clear error.</summary>
    private CStmt LowerWhileCapture(Item condItem, string capName, Item bodyItem,
        (Item body, string? errName)? elseInfo = null, CExpr? contPost = null)
    {
        var cond = LowerExpr(condItem);
        var ct = cond.Type.Unqualified;

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
            _symbols.EnterScope();
            if (capName != "_")
            {
                var okSym = _symbols.Declare(new Symbol { Name = capName, Kind = SymKind.Var, Type = eu.Payload });
                okStmts.Add(new DeclStmt(new List<LocalDecl> { new(okSym, new Member(capRef, "Value", false) { Type = eu.Payload }) }));
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
            _symbols.EnterScope();
            if (capName != "_")
            {
                var capSym = _symbols.Declare(new Symbol { Name = capName, Kind = SymKind.Var, Type = payloadType });
                thenStmts.Add(new DeclStmt(new List<LocalDecl> { new(capSym, payloadInit) }));
            }
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
            : new For(null, trueLit, contPost, new Block(loopBody));
    }

    /// <summary>Dispatch a <c>switch</c> statement: lower the subject once, then route a
    /// tagged-union subject (a value or pointer-to a registered <c>union(enum)</c>) to
    /// <see cref="LowerUnionSwitch"/> (the tag-discriminant + payload-capture path) and any other
    /// subject to the plain <see cref="LowerSwitch"/>.</summary>
    private CStmt LowerSwitchStmt(Item subjectItem, Item prongsItem)
    {
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
            if (prongItem.Content is Zig.ProngCapture or Zig.ProngCaptureRef)
            {
                throw new IrUnsupportedException(
                    "zig switch payload capture `|x|` is only valid on a tagged-union switch");
            }
            // A prong body is a braced Block (`=> { … }`) or a bare expression (`=> expr`, which in
            // a STATEMENT switch is an expression statement, e.g. `1 => doThing()`).
            Item caseVals;
            List<CStmt> body;
            switch (prongItem.Content)
            {
                case Zig.Prong p:      caseVals = p.Arg0;  body = new List<CStmt> { LowerBlock(p.Arg2) }; break;
                case Zig.ProngExpr pe: caseVals = pe.Arg0; body = new List<CStmt> { new ExprStmt(LowerExpr(pe.Arg2)) }; break;
                default: throw new IrUnsupportedException("zig switch prong: " + (prongItem.Content?.GetType().Name ?? "null"));
            }
            var labels = LowerCaseVals(caseVals, subject.Type); // case values compare against the subject
            if (!EndsInJump(body)) { body.Add(new Break()); }   // no Zig fall-through
            sections.Add(new SwitchSection(labels, body));
        }
        return new Switch(subject, sections);
    }

    /// <summary>Lower a <c>switch</c> over a tagged union: switch on the <see cref="TagFieldName"/>
    /// discriminant, with <c>.variant</c> case labels resolving against the tag enum. A
    /// <c>|x|</c> payload capture binds <c>x</c> to the matched variant's payload field (by value),
    /// and a by-reference <c>|*x|</c> capture (Milestone M, part 4) binds <c>x</c> to a <c>*T</c>
    /// pointer INTO that payload field, so <c>x.* = …</c> writes through to the (mutable) union — at
    /// the top of that prong's block. The subject is hoisted to a temp first (unless it is already a
    /// simple variable) so each capture re-reads it without re-evaluating a side-effecting subject
    /// expression.</summary>
    private CStmt LowerUnionSwitch(CExpr subject, Item prongsItem, ZigUnionInfo info)
    {
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
                var exprBody = new List<CStmt> { new ExprStmt(LowerExpr(pe.Arg2)) };
                if (!EndsInJump(exprBody)) { exprBody.Add(new Break()); }
                sections.Add(new SwitchSection(exprLabels, exprBody));
                continue;
            }
            Item caseVals; string? captureName; Item block; bool captureByRef;
            switch (prongItem.Content)
            {
                case Zig.Prong p:           caseVals = p.Arg0; captureName = null;        block = p.Arg2; captureByRef = false; break;
                case Zig.ProngCapture p:    caseVals = p.Arg0; captureName = Tok(p.Arg3); block = p.Arg5; captureByRef = false; break;
                case Zig.ProngCaptureRef p: caseVals = p.Arg0; captureName = Tok(p.Arg4); block = p.Arg6; captureByRef = true;  break;
                default: throw new IrUnsupportedException("zig switch prong: " + (prongItem.Content?.GetType().Name ?? "null"));
            }
            RejectUnionRange(caseVals, info);
            var labels = LowerCaseVals(caseVals, info.TagType);   // `.variant` → EnumConstRef(U_Tag.variant)

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
                var inner = LowerBlock(block);
                _symbols.ExitScope();
                var combined = new List<CStmt> { new DeclStmt(new List<LocalDecl> { new(capSym, capInit) }) };
                combined.AddRange(inner.Stmts);
                body = new List<CStmt> { new Block(combined) };
            }
            else
            {
                body = new List<CStmt> { LowerBlock(block) };
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
    private CStmt LowerForSlice(CExpr sliceExpr, string elemName, (string name, CExpr start)? index, Item bodyItem, bool byRef)
    {
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
                ? new SwitchLabel(LowerExprSink(lo, sink))
                : new SwitchLabel(LowerExprSink(lo, sink), LowerExprSink(hi, sink)));
        }
        return labels;
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
        Zig.CaseRangeOne or Zig.CaseRangeCons => true,
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
        Zig.IfExpr e             => e.Arg4.Content is Zig.LabeledBlock || e.Arg6.Content is Zig.LabeledBlock,
        Zig.SwitchExpr s         => SwitchExprNeedsStmt(s.Arg5),
        Zig.SwitchExprTrailing s => SwitchExprNeedsStmt(s.Arg5),
        // A value-position loop (`while/for … else`, Milestone Y part 2) ALWAYS needs the statement
        // lowering — a loop that yields via `break v` / an `else` value can't be a C# expression.
        Zig.WhileElseExpr or Zig.ForElseExpr or Zig.LabeledWhileElseExpr or Zig.LabeledForElseExpr => true,
        _ => false,
    };

    /// <summary>True when any prong of a switch EXPRESSION needs statements to yield its value — a
    /// block-bodied (<c>=&gt; { … }</c>) or capturing (<c>=&gt; |x| { … }</c>) prong, or a bare-expr
    /// prong whose value is itself a labeled value-block (<c>=&gt; blk: { … break :blk v; }</c>).</summary>
    private static bool SwitchExprNeedsStmt(Item prongsItem) =>
        Flatten(prongsItem).Any(p => p.Content switch
        {
            Zig.Prong or Zig.ProngCapture or Zig.ProngCaptureRef => true,
            Zig.ProngExpr pe => pe.Arg2.Content is Zig.LabeledBlock,
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
        Zig.WhileElseExpr or Zig.ForElseExpr or Zig.LabeledWhileElseExpr or Zig.LabeledForElseExpr
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
        bool isFor;
        Item condOrIter, blockItem, elseItem;
        string? elemName = null;
        switch (rhs.Content)
        {
            case Zig.WhileElseExpr w:        condOrIter = w.Arg2; blockItem = w.Arg4; elseItem = w.Arg6; isFor = false; break;
            case Zig.ForElseExpr f:          condOrIter = f.Arg2; elemName = Tok(f.Arg5); blockItem = f.Arg7; elseItem = f.Arg9; isFor = true; break;
            case Zig.LabeledWhileElseExpr w: label = Tok(w.Arg0); condOrIter = w.Arg4; blockItem = w.Arg6; elseItem = w.Arg8; isFor = false; break;
            case Zig.LabeledForElseExpr f:   label = Tok(f.Arg0); condOrIter = f.Arg4; elemName = Tok(f.Arg7); blockItem = f.Arg9; elseItem = f.Arg11; isFor = true; break;
            default: throw new IrUnsupportedException("internal: loop-value on " + (rhs.Content?.GetType().Name ?? "null"));
        }

        var n = _loopValueCounter++;
        var endLabel = "__lv" + n + "_end";
        var temp = _symbols.Declare(new Symbol { Name = "__lv" + n, Kind = SymKind.Var, Type = sink ?? CType.Int });
        var target = new LoopValueTarget { Temp = temp, EndLabel = endLabel, Label = label, Sink = sink, ResultType = sink };

        // Lower the loop with the value target active so a `break v` inside resolves to it. The cond /
        // iterable is lowered before the body (it can't `break`), so it never references the temp.
        _loopValues.Push(target);
        CStmt loop = isFor
            ? LowerForSlice(LowerExpr(condOrIter), elemName!, null, blockItem, byRef: false)
            : new While(LowerExpr(condOrIter), LowerBlock(blockItem));
        _loopValues.Pop();

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
        if (valueItem.Content is Zig.LabeledBlock lb)
        {
            return LowerLabeledValueBlock(Tok(lb.Arg0), lb.Arg2, rt.ResultType, blkTemp =>
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
    {
        var subject = LowerExpr(subjectItem);
        var uname = subject.Type.Unqualified switch
        {
            CType.Named nm => nm.Name,
            CType.Pointer { Pointee: var pe } when pe.Unqualified is CType.Named pn => pn.Name,
            _ => null,
        };
        if (uname is not null && _unions.ContainsKey(uname))
        {
            throw new IrUnsupportedException(
                "a tagged-union value-switch with block prongs (`const x = switch (u) { .v => blk: {…} }`) is not supported yet");
        }
        var sections = new List<SwitchSection>();
        foreach (var prongItem in Flatten(prongsItem))
        {
            if (prongItem.Content is not Zig.ProngExpr pe)
            {
                throw new IrUnsupportedException(
                    "a value-position switch prong must yield a value (`v => expr` or `v => blk: {… break :blk v;}`); " +
                    "a void block prong or a `|x|` capture in a switch expression is not supported yet");
            }
            var fill = FillValueTemp(pe.Arg2, rt);
            var labels = LowerCaseVals(pe.Arg0, subject.Type);
            sections.Add(new SwitchSection(labels, new List<CStmt> { fill, new Break() }));
        }
        return new Switch(subject, sections);
    }

    /// <summary>True when a lowered statement list provably ends control flow (so no
    /// synthetic <see cref="Break"/> is needed for a switch section). Mirrors the C# backend's
    /// own <c>Terminates</c>.</summary>
    private static bool EndsInJump(IReadOnlyList<CStmt> body) =>
        body.Count > 0 && Terminates(body[^1]);

    private static bool Terminates(CStmt s) => s switch
    {
        Return or Break or Continue or Goto => true,
        Block b => b.Stmts.Count > 0 && Terminates(b.Stmts[^1]),
        If f => f.Else is { } e && Terminates(f.Then) && Terminates(e),
        _ => false,
    };

    /// <summary>Lower <c>return e;</c>. In a <c>!T</c> function the value becomes an error
    /// union: <c>return error.Foo;</c> → an <see cref="ErrUnionErr"/>; a value that is ALREADY
    /// an error union (<c>return f();</c> where <c>f</c> returns <c>!U</c>) is returned as-is
    /// (Zig doesn't auto-unwrap); any plain value is wrapped in an <see cref="ErrUnionOk"/>.
    /// Outside an error-union function it is a plain <see cref="Return"/>.</summary>
    private CStmt LowerReturn(Item valueItem)
    {
        // `return blk: { … break :blk v; };` — a labeled value-block return (Milestone L, part 2).
        // Temp-fill against the function's return type, then `return` the result temp. (In an error-
        // union function the wrapping below would need to apply to the temp — deferred with a clear
        // error rather than silently returning an unwrapped value.)
        if (valueItem.Content is Zig.LabeledBlock lb)
        {
            if (_currentFnRet is CType.ErrorUnion)
            {
                throw new IrUnsupportedException(
                    "a labeled value-block `return blk: {…}` in an error-union (`!T`) function is not supported yet");
            }
            return LowerLabeledValueBlock(Tok(lb.Arg0), lb.Arg2, _currentFnRet,
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
                if (_currentFnHasErrdefer) { return new ZigErrorThrow(ErrorCode(errName)); }
                return new Return(new ErrUnionErr(ErrorCode(errName)) { Type = eu });
            }
            var v = LowerExpr(valueItem);
            if (v.Type.Unqualified is CType.ErrorUnion) { return new Return(v); }
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

        if (capName is null)
        {
            var fb = LowerExpr(fallbackItem);
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
        var fbCap = LowerExpr(fallbackItem);
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

    /// <summary>Recognize a control-flow <c>catch</c>/<c>orelse</c> fallback (Milestone N, part 6) —
    /// <c>a catch return [v]</c> / <c>a orelse return [v]</c> — yielding the left operand, whether it
    /// is a <c>catch</c> (vs <c>orelse</c>), and the optional return value (null = <c>return;</c>).</summary>
    private static bool IsControlFlowFallback(Item it, out Item lhs, out bool isCatch, out Item? retVal)
    {
        switch (it.Content)
        {
            case Zig.OrElseReturn r:     lhs = r.Arg0; isCatch = false; retVal = r.Arg3; return true;
            case Zig.OrElseReturnVoid r: lhs = r.Arg0; isCatch = false; retVal = null;   return true;
            case Zig.CatchReturn r:      lhs = r.Arg0; isCatch = true;  retVal = r.Arg3; return true;
            case Zig.CatchReturnVoid r:  lhs = r.Arg0; isCatch = true;  retVal = null;   return true;
            default: lhs = it; isCatch = false; retVal = null; return false;
        }
    }

    /// <summary>Lower a control-flow <c>catch</c>/<c>orelse</c> fallback (Milestone N, part 6): <c>a
    /// catch return [v]</c> / <c>a orelse return [v]</c>. The left operand — an error union (for
    /// <c>catch</c>) or an optional (for <c>orelse</c>) — is hoisted to a single-eval temp; on the
    /// error / none path the <c>return</c> runs as an EARLY-OUT (lowered via
    /// <see cref="LowerReturn"/>/<see cref="LowerReturnVoid"/>, so it wraps correctly in a <c>!T</c>
    /// function — incl. <c>return error.X</c>). On the success path the unwrapped payload is consumed
    /// by <paramref name="bind"/> (a decl initializer binds it; an expression-statement passes null
    /// and discards it). Emitted as <c>{ var __cf = a; if (Cond.B(&lt;none/error&gt;)) { return …; }
    /// [bind(payload)] }</c>.</summary>
    private CStmt LowerControlFlowFallback(Item lhsItem, bool isCatch, Item? retValItem, Func<CExpr, CStmt>? bind)
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
                throw new IrUnsupportedException("zig `catch return` requires an error-union left operand");
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
            throw new IrUnsupportedException("zig `orelse return` requires an optional left operand");
        }

        var ret = retValItem is null ? LowerReturnVoid() : LowerReturn(retValItem);
        pre.Add(new If(test, new Block(new List<CStmt> { ret }), null));
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
        var savedBuf = _hoist;
        var savedImpure = _hoistImpureSeen;
        _hoist = new List<CStmt>();
        _hoistImpureSeen = false;
        try
        {
            var stmt = lower();
            if (_hoist.Count == 0) { return stmt; }
            var seq = new List<CStmt>(_hoist) { stmt };
            return new Seq(seq);
        }
        finally
        {
            _hoist = savedBuf;
            _hoistImpureSeen = savedImpure;
        }
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
