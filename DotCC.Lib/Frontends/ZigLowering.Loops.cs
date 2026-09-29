#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Loops: <c>while</c> (with a capture, a continue expression, an <c>else</c>), <c>for</c> over a range, a
/// slice or several objects at once, <c>inline</c> loops unrolled at lowering time, and the break targets a
/// <c>break</c> inside a <c>switch</c> needs. One concern of the <see cref="ZigLowering"/> binder.</summary>
internal sealed partial class ZigLowering
{
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

            // `inline for (…) |…| { … break; } else { … }` (std.json.static's innerParse, task #210): the copies, then the
            // else body, which a `break` in any copy jumps past; a copy that always breaks leaves no else to reach.
            case Zig.StmtForSliceElse cfe when TryComptimeIterable(cfe.Arg2, out var cle):
                return UnrollComptimeFor(new[] { (cle, Tok(cfe.Arg5)) }, cfe.Arg7, cfe.Arg9);
            case Zig.StmtForMultiElse cme when FirstForObject(cme.Arg2) is { } firstE0 && TryComptimeIterable(firstE0, out _):
                return UnrollComptimeMultiFor(cme.Arg2, cme.Arg5, cme.Arg7, cme.Arg9);
            case Zig.StmtForMultiTrailElse cte when FirstForObject(cte.Arg2) is { } firstE1 && TryComptimeIterable(firstE1, out _):
                return UnrollComptimeMultiFor(cte.Arg2, cte.Arg6, cte.Arg8, cte.Arg10);
            case Zig.StmtForMultiElse mme:      return UnrollMixedInlineFor(mme.Arg2, mme.Arg5, mme.Arg7, mme.Arg9);
            case Zig.StmtForMultiTrailElse mte: return UnrollMixedInlineFor(mte.Arg2, mte.Arg6, mte.Arg8, mte.Arg10);

            // `inline for (a, b, 0.., …) |x, y, i, …|` — comptime lists walked in lockstep (road-to-zig-std S6; any number
            // of them since task #108). Measured in the pinned std the pair `(field_names, field_types)` is the DOMINANT
            // member-list shape (17 uses); `(list, 0..)` binds the list's own indices. See UnrollComptimeMultiFor.
            case Zig.StmtForMulti cm when FirstForObject(cm.Arg2) is { } first0 && TryComptimeIterable(first0, out _):
                return UnrollComptimeMultiFor(cm.Arg2, cm.Arg5, cm.Arg7);
            case Zig.StmtForMultiTrail ct when FirstForObject(ct.Arg2) is { } first1 && TryComptimeIterable(first1, out _):
                return UnrollComptimeMultiFor(ct.Arg2, ct.Arg6, ct.Arg8);
            // `inline for (s.ptrs, &ptrs, field_types) |in, *out, field_type|` (std.MultiArrayList.Slice.subslice, task
            // #108): fixed-length arrays in lockstep with comptime lists, led by an array. See UnrollMixedInlineFor.
            case Zig.StmtForMulti mm:      return UnrollMixedInlineFor(mm.Arg2, mm.Arg5, mm.Arg7);
            case Zig.StmtForMultiTrail mt: return UnrollMixedInlineFor(mt.Arg2, mt.Arg6, mt.Arg8);

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
                // A name, or a field of one (std.MultiArrayList's `inline for (sizes.bytes) |size|`, task #108): each copy
                // reads it again without a side effect.
                if (!IsStableArrayRef(operand))
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
                    + "slice, and a bare/expr-cont `inline while` are not supported yet"
                    + (_currentFnName.Length > 0 ? $" (in '{_currentFnName}', a {loopItem.Content?.GetType().Name})" : ""));
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
                using var symbolScope = EnterSymbolScope();
                var body = LowerStmt(bodyItem);
                symbolScope.Dispose();
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
            using var symbolScope = EnterSymbolScope();
            // A comptime-known capture (a counted range's index) IS its value in every comptime question,
            // as zig has it: `const block_x_len = block_len / (1 << j); comptime if (block_x_len < 4) break;`
            // in std.mem.findScalarPos folds through it.
            var init = initFor(k);
            // An element of a comptime-known aggregate (a const evaluated at compile time) folds through the interpreter.
            var folded = captureType.Unqualified is CType.Prim { Integer: true }
                ? _ir.ConstEval(init) ?? (_ir.EvalComptimeValue(init) is IrModule.CtInt { Value: var big } && big >= long.MinValue && big <= long.MaxValue ? (long)big : null)
                : null;
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
            symbolScope.Dispose();
            // `inline for (0..2) |_|` discards the index: no declaration (an unused `_` local is CS0219).
            var copy = captureName == "_" ? new List<CStmt> { body }
                : new List<CStmt> { new DeclStmt(new List<LocalDecl> { new(sym, init) }), body };
            if (!unroll.Add(new Block(copy))) { break; }
        }
        return unroll.Finish();
    }
    /// <summary>Unroll a <c>for</c> over a TUPLE (task #100): one copy of the body per element, its capture declared at that
    /// element's own type (a tuple of <c>.{ "one", 1 }</c> and <c>.{ "three", 3 }</c> holds two different types), with the
    /// optional index capture its start plus the element's position. zig only iterates a tuple at comptime, which is where
    /// std does it (a comptime-evaluated block's callee); a by-reference capture is not modeled.</summary>
    private CStmt UnrollTupleFor(CExpr tupleExpr, CType.Tuple tuple, string elemName, bool byRef, (string name, CExpr start)? index,
        Item bodyItem)
    {
        if (byRef)
        {
            throw new IrUnsupportedException($"zig `for` over a tuple with a by-reference capture `|*{elemName}|` is not supported");
        }
        var pre = new List<CStmt>();
        var tupleRef = tupleExpr;
        if (tupleExpr is not VarRef)
        {
            var tmp = _symbols.Declare(new Symbol { Name = "__tup", Kind = SymKind.Var, Type = tupleExpr.Type });
            pre.Add(new DeclStmt(new List<LocalDecl> { new(tmp, tupleExpr) }));
            tupleRef = new VarRef(tmp) { Type = tupleExpr.Type, IsLValue = true };
        }
        var unroll = new InlineUnroll(_blockLabelCounter++);
        for (var k = 0; k < tuple.Elements.Count; k++)
        {
            using var symbolScope = EnterSymbolScope();
            var copy = new List<CStmt>();
            if (elemName != "_")
            {
                var elemType = tuple.Elements[k];
                var elemSym = _symbols.Declare(new Symbol { Name = elemName, Kind = SymKind.Var, Type = elemType });
                copy.Add(new DeclStmt(new List<LocalDecl> { new(elemSym, new TupleIndex(tupleRef, k, elemType) { Type = elemType }) }));
            }
            if (index is { name: var indexName, start: var start } && indexName != "_")
            {
                var position = new LitInt(k.ToString(System.Globalization.CultureInfo.InvariantCulture), k) { Type = CType.ULong };
                CExpr at = _ir.ConstEval(start) is 0 ? position : new Binary(BinOp.Add, start, position) { Type = CType.ULong };
                var indexSym = _symbols.Declare(new Symbol
                {
                    Name = indexName, Kind = SymKind.Var, Type = CType.ULong,
                    IsConstexpr = _ir.ConstEval(at) is not null, ConstValue = _ir.ConstEval(at) ?? 0,
                });
                RecordValueBits(indexSym, 64, null);
                copy.Add(new DeclStmt(new List<LocalDecl> { new(indexSym, at) }));
            }
            _inlineUnrollDepth++;
            try { copy.Add(LowerStmt(bodyItem)); }
            finally { _inlineUnrollDepth--; }
            symbolScope.Dispose();
            if (!unroll.Add(new Block(copy))) { break; }
        }
        pre.Add(unroll.Finish());
        return pre.Count == 1 ? pre[0] : new Block(pre);
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
        if (isContinue && t.IsBlock)
        {
            throw new IrUnsupportedException($"`continue :{label}` names a labeled block, not a loop");
        }
        if (isContinue) { t.ContUsed = true; return new Goto(t.ContLabel); }
        t.BreakUsed = true;
        return new Goto(t.BreakLabel);
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
            using (EnterSymbolScope())
            {
                if (capName != "_" && contPost is null)
                {
                    okStmts.Add(BindCapture(eu.Payload, new Member(capRef, "Value", false) { Type = eu.Payload }));
                }
                okStmts.Add(LowerStmt(bodyItem));
            }

            var errStmts = new List<CStmt>();
            using (EnterSymbolScope())
            {
                if (errName != "_")
                {
                    var errSym = _symbols.Declare(new Symbol { Name = errName, Kind = SymKind.Var, Type = CType.ErrorSet });
                    errStmts.Add(new DeclStmt(new List<LocalDecl> { new(errSym, new Member(capRef, "Code", false) { Type = CType.ErrorSet }) }));
                }
                errStmts.Add(LowerStmt(eErrBody));
                errStmts.Add(new Break());
            }

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
                test = capRef;        // a pointer condition tests non-null
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
            using (EnterSymbolScope())
            {
                if (capName != "_" && contPost is null) { thenStmts.Add(BindCapture(payloadType, payloadInit)); }
                thenStmts.Add(LowerStmt(bodyItem));
            }

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
    /// <summary>How many <c>inline</c> loops are being unrolled around the current statement.</summary>
    private int _inlineUnrollDepth;
    /// <summary>True for a runtime loop statement (every <c>LoopStmt</c> form), which gets an unlabeled
    /// break target (<see cref="LowerLoopWithBreakTarget"/>).</summary>
    private static bool IsRuntimeLoopStmt(object? content) => content is
        Zig.StmtWhile or Zig.StmtWhileElse or Zig.StmtWhileCont or Zig.StmtWhileContAssign or Zig.StmtWhileContBlock
        or Zig.StmtWhileCapture or Zig.StmtWhileCaptureElse or Zig.StmtWhileCaptureErrElse
        or Zig.StmtWhileCaptureCont or Zig.StmtWhileCaptureContAssign
        or Zig.StmtForRange or Zig.StmtForSlice or Zig.StmtForSliceRef or Zig.StmtForMulti or Zig.StmtForMultiTrail
        or Zig.StmtForSliceElse or Zig.StmtForMultiElse or Zig.StmtForMultiTrailElse;
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
    /// <summary>One object of a multi-object <c>for</c>: an expression walked element by element, or a RANGE whose capture
    /// is the running index (<see cref="End"/> is null for an open <c>N..</c>).</summary>
    private sealed record ForObject(Item Expr, bool IsRange, Item? End);
    /// <summary>The expression of a multi-object <c>for</c>'s first object when it is a plain expression, or null.</summary>
    private static Item? FirstForObject(Item objsItem)
        => Flatten(objsItem) is [{ Content: Zig.ForObj first }, ..] ? first.Arg0 : null;
    /// <summary>The objects and captures of a multi-object <c>for</c> (task #108), which must pair up one to one; a range's
    /// index capture cannot be taken by reference.</summary>
    private (List<ForObject> Objects, List<(string Name, bool ByRef)> Captures) DecomposeForMulti(Item objsItem, Item capsItem)
    {
        var objects = Flatten(objsItem).Select(o => o.Content switch
        {
            Zig.ForObj e      => new ForObject(e.Arg0, false, null),
            Zig.ForObjFrom r  => new ForObject(r.Arg0, true, null),
            Zig.ForObjRange r => new ForObject(r.Arg0, true, r.Arg2),
            _ => throw new IrUnsupportedException("zig multi-object `for`: unexpected object " + (o.Content?.GetType().Name ?? "null")),
        }).ToList();
        var captures = Flatten(capsItem).Select(c => c.Content switch
        {
            Zig.ForCapVal v => (Name: Tok(v.Arg0), ByRef: false),
            Zig.ForCapRef r => (Name: Tok(r.Arg1), ByRef: true),
            _ => throw new IrUnsupportedException("zig multi-object `for`: unexpected capture " + (c.Content?.GetType().Name ?? "null")),
        }).ToList();
        if (objects.Count != captures.Count)
        {
            throw new CompileException($"zig: a `for` over {objects.Count} objects needs {objects.Count} captures; it has {captures.Count}");
        }
        for (var k = 0; k < objects.Count; k++)
        {
            if (objects[k].IsRange && captures[k].ByRef)
            {
                throw new CompileException($"zig: the index capture `{captures[k].Name}` of a range cannot be taken by reference (`|*{captures[k].Name}|`)");
            }
        }
        return (objects, captures);
    }
    /// <summary>Lower a runtime multi-object <c>for</c> (task #108): <c>for (s, N..) |x, i|</c> is the slice walked with its
    /// index (<see cref="LowerForSlice"/>, the shape std writes most); any other shape walks every object in lockstep
    /// (<see cref="LowerForParallel"/>).</summary>
    private CStmt LowerForMulti(Item objsItem, Item capsItem, Item bodyItem)
    {
        var (objects, captures) = DecomposeForMulti(objsItem, capsItem);
        if (objects is [{ IsRange: false } slice, { IsRange: true, End: null } index])
        {
            return LowerForSlice(LowerExpr(slice.Expr), captures[0].Name, (captures[1].Name, LowerExpr(index.Expr)), bodyItem,
                byRef: captures[0].ByRef);
        }
        return LowerForParallel(objects, captures, bodyItem);
    }
    /// <summary>Unroll an <c>inline for</c> over comptime lists walked in lockstep (road-to-zig-std S6; any number of lists
    /// since task #108): each object a comptime list, or an index range starting at 0 (the list's own indices). A comptime
    /// list paired with a runtime slice cannot be unrolled at all, and a comptime list has no storage to capture by
    /// reference, so each is named rather than left to a downstream type error.</summary>
    private CStmt UnrollComptimeMultiFor(Item objsItem, Item capsItem, Item bodyItem, Item? elseItem = null)
    {
        var (objects, captures) = DecomposeForMulti(objsItem, capsItem);
        var lists = new List<(ZigComptimeList List, string Name)>(objects.Count);
        ZigComptimeList? first = null;
        for (var k = 0; k < objects.Count; k++)
        {
            if (captures[k].ByRef)
            {
                throw new IrUnsupportedException(
                    $"`inline for` over comptime lists cannot capture `{captures[k].Name}` by reference: a comptime list has no storage");
            }
            ZigComptimeList list;
            if (objects[k].IsRange)
            {
                if (objects[k].End is not null || _ir.ConstEval(LowerExpr(objects[k].Expr)) is not 0 || first is null)
                {
                    throw new IrUnsupportedException(
                        "`inline for` over a comptime list with an index capture must start the index at 0 "
                        + "(`for (list, 0..) |x, i|`)");
                }
                list = IndexList(first.Count);
            }
            else if (!TryComptimeIterable(objects[k].Expr, out list))
            {
                throw new IrUnsupportedException(
                    "`inline for` over parallel operands requires every operand to be a comptime list "
                    + $"(`{first?.Label}` is one; operand {k + 1} is not)");
            }
            first ??= list;
            lists.Add((list, captures[k].Name));
        }
        return UnrollComptimeFor(lists.ToArray(), bodyItem, elseItem);
    }
    /// <summary>Lower a runtime lockstep <c>for (a, b, c) |x, y, z| body</c> (road-to-zig-std G5): one index walks every
    /// object, each capture a per-iteration copy of its object's element (<c>|*x|</c>: a pointer into it). Each object is a
    /// slice (an array, or a pointer to one, walks as a slice over it) read once into a temp, or a RANGE (task #108) whose
    /// capture is its start plus the index. The walk's length is the first slice's (zig asserts equal lengths; dotcc does
    /// not check, its ReleaseFast stance on safety checks), or a bounded range's when there is no slice. A <c>_</c> capture
    /// binds nothing. With an <paramref name="elseItem"/> (<c>for (…) |…| body else elsebody</c>, task #108) the loop's own
    /// exit sets a flag the else is guarded by, after the loop: a <c>break</c> skips it, and a <c>break</c> inside the else
    /// still reaches an OUTER loop, as in zig.</summary>
    private CStmt LowerForParallel(IReadOnlyList<ForObject> objects, IReadOnlyList<(string Name, bool ByRef)> captures, Item bodyItem,
        Item? elseItem = null)
    {
        var pre = new List<CStmt>();
        var walks = new List<(CExpr? Slice, CType.Slice? Type, CExpr? Start)>(objects.Count);
        CExpr? len = null;
        CExpr Pinned(CExpr value, string name)
        {
            if (value is VarRef || _ir.ConstEval(value) is not null) { return value; }
            var tmp = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = value.Type });
            pre.Add(new DeclStmt(new List<LocalDecl> { new(tmp, value) }));
            return new VarRef(tmp) { Type = value.Type, IsLValue = true };
        }
        foreach (var obj in objects)
        {
            if (obj.IsRange)
            {
                var start = Pinned(LowerExprSink(obj.Expr, CType.ULong), "__lo");
                if (obj.End is { } endItem)
                {
                    var end = Pinned(LowerExprSink(endItem, CType.ULong), "__hi");
                    len ??= new Binary(BinOp.Sub, end, start) { Type = CType.ULong };
                }
                walks.Add((null, null, start));
                continue;
            }
            var value = LowerExpr(obj.Expr);
            // An array, or a pointer to one (`&used`), walks as a slice over it.
            if (value.Type.Unqualified is CType.Array arr) { value = CoerceToSlice(value, new CType.Slice(arr.Element)); }
            else if (value.Type.Unqualified is CType.Pointer { Pointee: var pte } && pte.Unqualified is CType.Array parr)
            {
                value = CoerceToSlice(value, new CType.Slice(parr.Element));
            }
            if (value.Type.Unqualified is not CType.Slice slc)
            {
                throw new IrUnsupportedException(
                    $"zig multi-object `for`: each object must be a slice, an array or a range; got {value.Type.Describe()}");
            }
            CExpr sliceRef = value;
            if (value is not VarRef)
            {
                var tmp = _symbols.Declare(new Symbol { Name = "__s", Kind = SymKind.Var, Type = value.Type });
                pre.Add(new DeclStmt(new List<LocalDecl> { new(tmp, value) }));
                sliceRef = new VarRef(tmp) { Type = value.Type, IsLValue = true };
            }
            walks.Add((sliceRef, slc, null));
        }
        // The first slice's length wins over a bounded range's: it is the object zig checks the others against.
        if (walks.FirstOrDefault(w => w.Slice is not null) is { Slice: { } firstSlice })
        {
            len = new Member(firstSlice, "Len", false) { Type = CType.ULong, IsLValue = true };
        }
        if (len is null)
        {
            throw new CompileException("zig: a `for` over unbounded ranges only has no length (give a range an end, or add an object)");
        }
        using var symbolScope = EnterSymbolScope();
        var iSym = _symbols.Declare(new Symbol { Name = "__i", Kind = SymKind.Var, Type = CType.ULong });
        var iRef = new VarRef(iSym) { Type = CType.ULong, IsLValue = true };
        var init = new DeclStmt(new List<LocalDecl> { new(iSym, new LitInt("0", 0) { Type = CType.ULong }) });
        var cond = new Binary(BinOp.Lt, iRef, len) { Type = CType.Int };
        var post = new Unary(UnOp.PostInc, iRef) { Type = CType.ULong };
        var bodyStmts = new List<CStmt>();
        VarRef? natural = null;
        if (elseItem is not null)
        {
            var flag = _symbols.Declare(new Symbol { Name = "__natural", Kind = SymKind.Var, Type = CType.Bool });
            natural = new VarRef(flag) { Type = CType.Bool, IsLValue = true };
            pre.Add(new DeclStmt(new List<LocalDecl> { new(flag, new LitBool(false) { Type = CType.Bool }) }));
            bodyStmts.Add(new If(new Unary(UnOp.LogNot, cond) { Type = CType.Int }, new Block(new List<CStmt>
            {
                new ExprStmt(new Assign(null, natural, new LitBool(true) { Type = CType.Bool }) { Type = CType.Bool }),
                new Break(),
            }), null));
        }
        for (var k = 0; k < walks.Count; k++)
        {
            if (captures[k].Name == "_") { continue; }
            var (sref, st, start) = walks[k];
            CType capType;
            CExpr capInit;
            if (start is not null)
            {
                capType = CType.ULong;
                capInit = start is LitInt { Value: 0 } ? iRef : new Binary(BinOp.Add, start, iRef) { Type = CType.ULong };
            }
            else if (sref is not null && st is not null)
            {
                var ptr = new Member(sref, "Ptr", false) { Type = new CType.Pointer(st.Element) };
                var elem = new DotCC.Ir.Index(ptr, iRef) { Type = st.Element, IsLValue = true };
                // `|*x|` binds a pointer INTO the object (`x.* = …` writes through), `|x|` a per-iteration copy.
                capType = captures[k].ByRef ? new CType.Pointer(st.Element) : st.Element;
                capInit = captures[k].ByRef ? new Unary(UnOp.AddrOf, elem) { Type = capType } : elem;
            }
            else
            {
                throw new System.InvalidOperationException("a multi-object `for` walk is neither a range nor a slice");
            }
            var sym = _symbols.Declare(new Symbol { Name = captures[k].Name, Kind = SymKind.Var, Type = capType });
            bodyStmts.Add(new DeclStmt(new List<LocalDecl> { new(sym, capInit) }));
        }
        var userBody = LowerStmt(bodyItem);
        bodyStmts.Add(userBody);
        symbolScope.Dispose();
        var forStmt = new For(init, natural is null ? cond : null, post, new Block(bodyStmts));
        if (pre.Count == 0 && elseItem is null) { return forStmt; }
        pre.Add(forStmt);
        if (elseItem is not null && natural is not null)
        {
            // With no `break` out of the body the loop only ends naturally, so the else follows unguarded: C# then sees
            // an else that returns end the function (`for (…) { if (c) return i; } else return 50;`, CS0161 otherwise).
            var elseStmt = LowerStmt(elseItem);
            pre.Add(BreaksOut(userBody) ? new If(natural, elseStmt, null) : elseStmt);
        }
        return new Block(pre);
    }
    /// <summary>Lower the statement <c>while (c) body else elsebody</c> (task #130, std.bit_set's findFirstSet) as the
    /// for-else is (<see cref="LowerForParallel"/>): <c>while (true) { if (!c) { __natural = true; break; } body }</c>, then
    /// the else when the condition, not a <c>break</c>, ended the loop. A <c>continue</c> in the body re-tests the condition,
    /// as zig's does. With no <c>break</c> out of the body the else follows unguarded, so C# sees a returning else end
    /// the function.</summary>
    private CStmt LowerWhileElseStmt(Item condItem, Item bodyItem, Item elseItem)
    {
        using var symbolScope = EnterSymbolScope();
        // Numbered: a nested while-else's flag would otherwise shadow its enclosing one's, which C# refuses (CS0136).
        var flag = _symbols.Declare(new Symbol { Name = "__natural" + _loopLabelCounter++, Kind = SymKind.Var, Type = CType.Bool });
        var natural = new VarRef(flag) { Type = CType.Bool, IsLValue = true };
        var exit = new If(new Unary(UnOp.LogNot, LowerExpr(condItem)) { Type = CType.Int }, new Block(new List<CStmt>
        {
            new ExprStmt(new Assign(null, natural, new LitBool(true) { Type = CType.Bool }) { Type = CType.Bool }),
            new Break(),
        }), null);
        var userBody = LowerStmt(bodyItem);
        var loop = new While(new LitBool(true) { Type = CType.Bool }, new Block(new List<CStmt> { exit, userBody }));
        var elseStmt = LowerStmt(elseItem);
        symbolScope.Dispose();
        return new Block(new List<CStmt>
        {
            new DeclStmt(new List<LocalDecl> { new(flag, new LitBool(false) { Type = CType.Bool }) }),
            loop,
            BreaksOut(userBody) ? new If(natural, elseStmt, null) : elseStmt,
        });
    }
    /// <summary>True when <paramref name="s"/> contains a <c>break</c> that leaves the loop it sits in, one not inside a
    /// nested loop or switch of its own (a labeled break lowers to a <c>goto</c> past the loop, so it counts too).</summary>
    private static bool BreaksOut(CStmt? s) => s switch
    {
        Break or Goto => true,
        Block b => b.Stmts.Any(BreaksOut),
        Seq q => q.Stmts.Any(BreaksOut),
        If f => BreaksOut(f.Then) || BreaksOut(f.Else),
        Labeled l => BreaksOut(l.Body),
        For or While or DoWhile or Switch => false,
        _ => false,
    };
    /// <summary>Lower a for-over-slice — <c>for (s) |x| body</c> and (when <paramref name="index"/>
    /// is set) <c>for (s, START..) |x, i| body</c> — to the C IR <c>for</c>:
    /// <code>{ var __s = s; for (usize __i = 0; __i &lt; __s.Len; __i++) { var x = __s.Ptr[__i];
    /// [var i = __i + START;] body } }</code>
    /// The element capture <c>x</c> is a per-iteration copy (Zig's by-value <c>|x|</c>; the by-ref
    /// <c>|*x|</c> form is deferred). The slice is hoisted to <c>__s</c> unless it is already a bare
    /// variable, so <c>.Len</c>/<c>.Ptr</c> aren't re-evaluated with side effects.</summary>
    private CStmt LowerForSlice(CExpr sliceExpr, string elemName, (string name, CExpr start)? index, Item bodyItem, bool byRef,
        int? elemBits = null)
    {
        // A TUPLE (std.StaticStringMap's `for (kvs_list, 0..) |kv, i|` over `.{ .{ "one", 1 }, .{ "three", 3 } }`, task #100):
        // its elements differ in type, so the loop can only be unrolled, as an `inline for` is.
        if (sliceExpr.Type.Unqualified is CType.Tuple tuple)
        {
            // Only at comptime, as zig has it: a runtime loop cannot know which field it reads. Outside a comptime context
            // the function may still be one only ever called at comptime (std.StaticStringMap's initSortedKVs, called from
            // initComptime's `comptime { }` block), so a runtime call reaching it is what is rejected, once the whole call
            // graph is known (as task #92's comptime returns are).
            if (_loweringForComptimeEval == 0 && _comptimeDepth == 0)
            {
                const string runtimeTupleFor = "zig: unable to resolve comptime value: tuple field index must be comptime-known "
                    + "(iterate a tuple with `inline for`, or at comptime)";
                if (_currentFnSym is not { } tupleOwner) { throw new CompileException(runtimeTupleFor); }
                _comptimeReturnFns.TryAdd(tupleOwner, runtimeTupleFor);
            }
            return UnrollTupleFor(sliceExpr, tuple, elemName, byRef, index, bodyItem);
        }
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

        using var symbolScope = EnterSymbolScope();
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
            RecordValueBits(idxSym, 64, null);
            bodyStmts.Add(new DeclStmt(new List<LocalDecl> { new(idxSym, idxInit) }));
        }
        bodyStmts.Add(LowerStmt(bodyItem));
        symbolScope.Dispose();

        var forStmt = new For(init, cond, post, new Block(bodyStmts));
        if (pre.Count == 0) { return forStmt; }
        pre.Add(forStmt);
        return new Block(pre);
    }
}
