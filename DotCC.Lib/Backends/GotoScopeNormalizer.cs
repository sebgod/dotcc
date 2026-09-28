#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace DotCC.Backends;

using DotCC.Ir;

/// <summary>
/// C#-backend IR pass: make every <c>goto</c> target legal under C# label
/// scoping. C lets a <c>goto</c> jump INTO a nested block (chibi:
/// <c>goto adjust;</c> from outside the <c>if</c> whose body declares
/// <c>adjust:</c>); C# scopes a label to its enclosing block, so that render
/// is CS0159.
/// </summary>
/// <remarks>
/// The normalization hoists the labeled TAIL of the offending block out to the
/// parent, one level at a time, until the label is visible from every goto:
/// <code>
///   if (P) { head; L: tail; }   rest;
/// </code>
/// becomes
/// <code>
///   if (P) { head; goto L; }  goto __skip;  L: tail;  __skip: ;  rest;
/// </code>
/// Control-flow equivalent: head still falls into the tail (via the explicit
/// goto), every other exit of the <c>if</c> skips the tail, and the tail's own
/// fall-through reaches <c>rest</c> exactly as block-exit did. If-arms and plain
/// nested blocks are hoisted through directly. A label that must leave a LOOP
/// body (CPython's dtoa.c jumps into a <c>for</c> body at <c>bump_up</c>) first
/// has that loop lowered to labels and gotos at its own level (see
/// <see cref="FlattenLoop"/>), which leaves the body a plain nested block the
/// next step hoists through. A label that must leave a SWITCH body fails loudly
/// (the switch-internal cases are RenderSwitch's own hoisting machinery), as does
/// a <c>for</c> with a declaration in its init.
/// Functions with no scope violation pass through untouched (the common case —
/// the pass costs one read-only scan).
/// </remarks>
internal static class GotoScopeNormalizer
{
    public static Block Normalize(Block body)
    {
        // Each hoist lifts one label one block level (or flattens one loop); a C
        // function body has bounded nesting, so this converges — the guard is a
        // backstop sized for a many-label function like dtoa.c's _Py_dg_dtoa.
        // The skip labels already placed, so a label hoisted through several
        // blocks gets a distinct skip label at each level.
        var skips = new HashSet<string>(StringComparer.Ordinal);
        for (var step = 0; step < 256; step++)
        {
            var label = FindViolation(body);
            if (label == null) { return body; }
            // The step number is unique within the function, so it names the
            // labels of the (at most one) loop this step flattens, and a skip
            // label whose plain name is taken.
            var h = new Hoister(label, step, skips);
            body = h.Rewrite(body);
            if (!h.Done)
            {
                throw new IrUnsupportedException(
                    $"goto into a block the normalizer cannot hoist through (label '{label}' — a switch body, a `for` with a declaration in its init, or an unhandled nesting shape)");
            }
        }
        throw new IrUnsupportedException("goto/label normalization did not converge");
    }

    /// <summary>Find a label declared in a block that does NOT enclose one of
    /// its gotos (C# visibility rule: the label's scope chain must be a prefix
    /// of the goto's). Labels inside a <see cref="Switch"/> are skipped —
    /// RenderSwitch owns those via its goto-case / section-hoist machinery.</summary>
    private static string? FindViolation(Block body)
    {
        var labelChain = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        var inSwitch = new HashSet<string>(StringComparer.Ordinal);
        var gotos = new List<(string Label, List<object> Chain)>();
        var chain = new List<object>();
        var switchDepth = 0;

        void Walk(CStmt? s)
        {
            switch (s)
            {
                case null: return;
                case Block b:
                    chain.Add(b);
                    foreach (var st in b.Stmts) { Walk(st); }
                    chain.RemoveAt(chain.Count - 1);
                    return;
                case Seq q: // braceless — no scope of its own
                    foreach (var st in q.Stmts) { Walk(st); }
                    return;
                case Labeled l:
                    labelChain[l.Name] = new List<object>(chain);
                    if (switchDepth > 0) { inSwitch.Add(l.Name); }
                    Walk(l.Body);
                    return;
                case Goto g: gotos.Add((g.Label, new List<object>(chain))); return;
                case If f: Walk(f.Then); Walk(f.Else); return;
                case While w: Walk(w.Body); return;
                case DoWhile dw: Walk(dw.Body); return;
                case For fo: Walk(fo.Init); Walk(fo.Body); return;
                case SetjmpGuard sj: Walk(sj.TryBody); Walk(sj.CatchBody); return;
                // The capture's own goto-restart label/goto are backend-synthetic (never IR
                // nodes), so only its body carries user gotos/labels to normalize.
                case SetjmpCapture sc: Walk(sc.Body); return;
                case CaseLabelStmt cl: Walk(cl.Body); return;
                case Switch sw:
                    switchDepth++;
                    foreach (var sec in sw.Sections)
                    {
                        foreach (var st in sec.Body) { Walk(st); }
                    }
                    switchDepth--;
                    return;
                default: return;
            }
        }
        Walk(body);

        foreach (var (label, gchain) in gotos)
        {
            if (!labelChain.TryGetValue(label, out var lchain) || inSwitch.Contains(label)) { continue; }
            var visible = lchain.Count <= gchain.Count
                && !lchain.Where((blk, i) => !ReferenceEquals(blk, gchain[i])).Any();
            if (!visible) { return label; }
        }
        return null;
    }

    /// <summary>One hoist of one label: finds the statement whose if-arm /
    /// nested block declares the label at top level, splits the arm there, and
    /// splices the tail (plus the skip jump) into the parent statement list.</summary>
    private sealed class Hoister
    {
        private readonly string _label;
        private readonly int _step;
        private readonly HashSet<string> _skips;
        public bool Done { get; private set; }

        public Hoister(string label, int step, HashSet<string> skips)
        {
            _label = label;
            _step = step;
            _skips = skips;
        }

        public Block Rewrite(Block b)
        {
            if (Done) { return b; }
            var stmts = new List<CStmt>(b.Stmts);
            for (var i = 0; i < stmts.Count; i++)
            {
                // A loop whose body declares the label at top level: lower the loop
                // in place, so its body becomes a plain nested block of this one.
                // A labeled loop (`retry: for (…) …`) keeps its label on the loop's
                // first statement, the top of its first iteration.
                var loop = stmts[i] is Labeled { Body: var inner } ? inner : stmts[i];
                if (DeclaresAtTop(LoopBody(loop)) && FlattenLoop(loop, _step) is { } flat)
                {
                    if (stmts[i] is Labeled own) { flat.Insert(0, new Labeled(own.Name, new Block(Array.Empty<CStmt>()))); }
                    stmts.RemoveAt(i);
                    stmts.InsertRange(i, flat);
                    Done = true;
                    return new Block(stmts) { Pos = b.Pos };
                }
                if (TryExtract(stmts[i], out var replaced, out var tail))
                {
                    var skip = _skips.Add($"__skip_{_label}") ? $"__skip_{_label}" : $"__skip{_step}_{_label}";
                    _skips.Add(skip);
                    var insert = new List<CStmt> { replaced, new Goto(skip) };
                    insert.AddRange(tail);
                    insert.Add(new Labeled(skip, new Block(Array.Empty<CStmt>())));
                    stmts.RemoveAt(i);
                    stmts.InsertRange(i, insert);
                    Done = true;
                    return new Block(stmts) { Pos = b.Pos };
                }
            }
            for (var i = 0; i < stmts.Count && !Done; i++) { stmts[i] = RewriteStmt(stmts[i]); }
            return new Block(stmts) { Pos = b.Pos };
        }

        /// <summary>When <paramref name="s"/> is an <c>if</c> (either arm) or a
        /// plain nested block whose TOP-LEVEL statements declare the label:
        /// produce the statement with the labeled tail replaced by
        /// <c>goto label;</c>, and the extracted tail.</summary>
        private bool TryExtract(CStmt s, out CStmt replaced, out List<CStmt> tail)
        {
            switch (s)
            {
                case If f when SplitArm(f.Then, out var thenHead, out tail!):
                    replaced = f with { Then = thenHead };
                    return true;
                case If f when f.Else is { } el && SplitArm(el, out var elseHead, out tail!):
                    replaced = f with { Else = elseHead };
                    return true;
                // An else-if chain: `if … else if (P) { L: tail }` — the nested If
                // hangs off the Else slot, not a block statement list. Extract
                // within it and rebuild the chain spine.
                case If f when f.Else is If nested && TryExtract(nested, out var newNested, out tail!):
                    replaced = f with { Else = newNested };
                    return true;
                case Block nb when SplitArm(nb, out var head, out tail!):
                    replaced = head;
                    return true;
            }
            replaced = s;
            tail = new List<CStmt>();
            return false;
        }

        /// <summary>Split a block at <c>label:</c> (top level only): head keeps
        /// everything before it plus the re-entry <c>goto label;</c>.</summary>
        private bool SplitArm(CStmt arm, out CStmt head, out List<CStmt> tail)
        {
            if (arm is Block ab)
            {
                for (var k = 0; k < ab.Stmts.Count; k++)
                {
                    if (ab.Stmts[k] is Labeled l && l.Name == _label)
                    {
                        tail = ab.Stmts.Skip(k).ToList();
                        var headStmts = ab.Stmts.Take(k).Append(new Goto(_label)).ToList();
                        head = new Block(headStmts) { Pos = ab.Pos };
                        return true;
                    }
                }
            }
            head = arm;
            tail = new List<CStmt>();
            return false;
        }

        /// <summary>Whether <paramref name="body"/> declares the label as one of its
        /// top-level statements (or is that labeled statement).</summary>
        private bool DeclaresAtTop(CStmt? body) => body switch
        {
            Labeled l => l.Name == _label,
            Block b => b.Stmts.Any(x => x is Labeled l && l.Name == _label),
            _ => false,
        };

        private CStmt RewriteStmt(CStmt s) => s switch
        {
            Block b => Rewrite(b),
            Seq q => new Seq(q.Stmts.Select(RewriteStmt).ToList()) { Pos = q.Pos },
            Labeled l => l with { Body = RewriteStmt(l.Body) },
            If f => f with { Then = RewriteStmt(f.Then), Else = f.Else is { } e ? RewriteStmt(e) : null },
            While w => w with { Body = RewriteStmt(w.Body) },
            DoWhile dw => dw with { Body = RewriteStmt(dw.Body) },
            For fo => fo with { Body = RewriteStmt(fo.Body) },
            SetjmpGuard sj => sj with
            {
                TryBody = sj.TryBody is { } tb ? RewriteStmt(tb) : null,
                CatchBody = sj.CatchBody is { } cb ? RewriteStmt(cb) : null,
            },
            SetjmpCapture sc => sc with { Body = RewriteStmt(sc.Body) },
            CaseLabelStmt cl => cl with { Body = RewriteStmt(cl.Body) },
            Switch sw => sw with
            {
                Sections = sw.Sections
                    .Select(sec => new SwitchSection(sec.Labels, sec.Body.Select(RewriteStmt).ToList()))
                    .ToList(),
            },
            _ => s,
        };
    }

    /// <summary>The body of a loop statement, or null for any other statement.</summary>
    private static CStmt? LoopBody(CStmt s) => s switch
    {
        While w => w.Body,
        DoWhile dw => dw.Body,
        For fo => fo.Body,
        _ => null,
    };

    /// <summary>
    /// Lower the loop <paramref name="s"/> to labels and gotos at its own level, so a
    /// label inside its body becomes hoistable (C lets a <c>goto</c> jump into a loop
    /// body; C# does not):
    /// <code>
    ///   for (init; cond; post) body
    /// </code>
    /// becomes the statements
    /// <code>
    ///   init; __loopN_top: ; if (!cond) goto __loopN_end; body'
    ///   __loopN_cont: ; post; goto __loopN_top; __loopN_end: ;
    /// </code>
    /// where <c>body'</c> is <c>body</c> with the loop's own <c>break</c> /
    /// <c>continue</c> (not those of a nested loop, nor the <c>break</c> of a nested
    /// switch) turned into <c>goto __loopN_end</c> / <c>goto __loopN_cont</c>.
    /// <c>while</c> and <c>do … while</c> lower the same way, and a label is emitted
    /// only when something jumps to it. A comma list in the init or post becomes one
    /// statement per operand. Null for a <c>for</c> whose init declares variables:
    /// hoisting the declaration to this level could collide with a sibling loop's.
    /// </summary>
    private static List<CStmt>? FlattenLoop(CStmt s, int n)
    {
        var top = $"__loop{n}_top";
        var cont = $"__loop{n}_cont";
        var end = $"__loop{n}_end";
        static CStmt Mark(string label) => new Labeled(label, new Block(Array.Empty<CStmt>()));
        CStmt ExitUnless(CExpr cond) => new If(new Unary(UnOp.LogNot, cond) { Type = CType.Int }, new Goto(end), null);
        var stmts = new List<CStmt>();
        CStmt body;
        switch (s)
        {
            case While w:
                body = RetargetJumps(w.Body, end, top);
                stmts.Add(Mark(top));
                stmts.Add(ExitUnless(w.Cond));
                stmts.Add(body);
                stmts.Add(new Goto(top));
                stmts.Add(Mark(end));
                return stmts;
            case DoWhile dw:
                body = RetargetJumps(dw.Body, end, cont);
                stmts.Add(Mark(top));
                stmts.Add(body);
                if (JumpsTo(body, cont)) { stmts.Add(Mark(cont)); }
                stmts.Add(new If(dw.Cond, new Goto(top), null));
                if (JumpsTo(body, end)) { stmts.Add(Mark(end)); }
                return stmts;
            case For fo:
                if (fo.Init is DeclStmt) { return null; }
                body = RetargetJumps(fo.Body, end, cont);
                if (fo.Init is ExprStmt { Expr: var init }) { stmts.AddRange(ExprStmts(init)); }
                else if (fo.Init is { } other) { stmts.Add(other); }
                stmts.Add(Mark(top));
                if (fo.Cond is { } c) { stmts.Add(ExitUnless(c)); }
                stmts.Add(body);
                if (JumpsTo(body, cont)) { stmts.Add(Mark(cont)); }
                if (fo.Post is { } post) { stmts.AddRange(ExprStmts(post)); }
                stmts.Add(new Goto(top));
                if (fo.Cond is not null || JumpsTo(body, end)) { stmts.Add(Mark(end)); }
                return stmts;
            default:
                return null;
        }
    }

    /// <summary>One expression statement per operand of a for-init / for-update comma
    /// list (a <see cref="CommaSeq"/> renders as <c>a, b</c>, legal only in a
    /// <c>for</c> header).</summary>
    private static IEnumerable<CStmt> ExprStmts(CExpr e)
        => e is CommaSeq cs ? cs.Items.Select(x => (CStmt)new ExprStmt(x)) : new CStmt[] { new ExprStmt(e) };

    /// <summary>Turn a loop body's own <c>break</c> / <c>continue</c> into gotos; a
    /// nested loop keeps both, a nested switch keeps its <c>break</c>.</summary>
    private static CStmt RetargetJumps(CStmt s, string breakLabel, string continueLabel)
    {
        CStmt Walk(CStmt st, bool inSwitch) => st switch
        {
            Break when !inSwitch => new Goto(breakLabel) { Pos = st.Pos },
            Continue => new Goto(continueLabel) { Pos = st.Pos },
            Block b => b with { Stmts = b.Stmts.Select(x => Walk(x, inSwitch)).ToList() },
            Seq q => q with { Stmts = q.Stmts.Select(x => Walk(x, inSwitch)).ToList() },
            Labeled l => l with { Body = Walk(l.Body, inSwitch) },
            If f => f with { Then = Walk(f.Then, inSwitch), Else = f.Else is { } e ? Walk(e, inSwitch) : null },
            CaseLabelStmt cl => cl with { Body = Walk(cl.Body, inSwitch) },
            SetjmpGuard sj => sj with
            {
                TryBody = sj.TryBody is { } tb ? Walk(tb, inSwitch) : null,
                CatchBody = sj.CatchBody is { } cb ? Walk(cb, inSwitch) : null,
            },
            SetjmpCapture sc => sc with { Body = Walk(sc.Body, inSwitch) },
            Switch sw => sw with
            {
                Sections = sw.Sections
                    .Select(sec => sec with { Body = sec.Body.Select(x => Walk(x, inSwitch: true)).ToList() })
                    .ToList(),
            },
            // A nested loop owns its break and continue.
            _ => st,
        };
        return Walk(s, inSwitch: false);
    }

    /// <summary>Whether <paramref name="s"/> contains a <c>goto</c> to
    /// <paramref name="label"/>.</summary>
    private static bool JumpsTo(CStmt? s, string label) => s switch
    {
        null => false,
        Goto g => g.Label == label,
        Block b => b.Stmts.Any(x => JumpsTo(x, label)),
        Seq q => q.Stmts.Any(x => JumpsTo(x, label)),
        Labeled l => JumpsTo(l.Body, label),
        If f => JumpsTo(f.Then, label) || JumpsTo(f.Else, label),
        While w => JumpsTo(w.Body, label),
        DoWhile dw => JumpsTo(dw.Body, label),
        For fo => JumpsTo(fo.Body, label),
        CaseLabelStmt cl => JumpsTo(cl.Body, label),
        SetjmpGuard sj => JumpsTo(sj.TryBody, label) || JumpsTo(sj.CatchBody, label),
        SetjmpCapture sc => JumpsTo(sc.Body, label),
        Switch sw => sw.Sections.Any(sec => sec.Body.Any(x => JumpsTo(x, label))),
        _ => false,
    };
}
