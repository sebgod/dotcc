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
/// next step hoists through.
/// <para>A label inside a switch is hoisted to the top level of its case section
/// (ceval's <c>PREDICTED(op)</c> label, inside the block of a <c>TARGET(op)</c>
/// case, reached from the other cases): from there RenderSwitch's own machinery
/// takes a goto from another section (<c>goto case</c>, or the shared-handler
/// hoist). A goto from outside the switch (fileio.c's <c>goto bad_mode</c> after
/// the loop around the switch) needs the label out of the switch: its tail leaves
/// the section, as a tail leaves an <c>if</c>, with the section's own
/// <c>break</c>s now jumping past it (see <see cref="Hoister.SplitSwitch"/>).</para>
/// A <c>for</c> with a declaration in its init fails loudly, as does a case tail
/// that must leave its switch but falls through into the next case.
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
        var renamed = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
        for (var step = 0; step < 256; step++)
        {
            var label = FindViolation(body);
            if (label == null) { return DeclareBeforeGotos(body); }
            // The step number is unique within the function, so it names the
            // labels of the (at most one) loop this step flattens, and a skip
            // label whose plain name is taken.
            var h = new Hoister(label, step, skips, renamed);
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
    /// of the goto's). A case section is a scope of the chain too; a label at a
    /// section's top level is left to RenderSwitch when every goto that cannot see
    /// it is in the same switch (its goto-case / section-hoist machinery).</summary>
    private static string? FindViolation(Block body)
    {
        var labelChain = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        var gotos = new List<(string Label, List<object> Chain)>();
        var chain = new List<object>();
        // Each case section's switch, by identity (sections are records, compared by value).
        var sectionSwitch = new Dictionary<object, Switch>(ReferenceEqualityComparer.Instance);

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
                    foreach (var sec in sw.Sections)
                    {
                        sectionSwitch[sec] = sw;
                        chain.Add(sec);
                        foreach (var st in sec.Body) { Walk(st); }
                        chain.RemoveAt(chain.Count - 1);
                    }
                    return;
                default: return;
            }
        }
        Walk(body);

        foreach (var (label, gchain) in gotos)
        {
            if (!labelChain.TryGetValue(label, out var lchain)) { continue; }
            var visible = lchain.Count <= gchain.Count
                && !lchain.Where((blk, i) => !ReferenceEquals(blk, gchain[i])).Any();
            if (visible) { continue; }
            if (SectionOfTopLevelLabel(lchain) is { } sec && sectionSwitch.TryGetValue(sec, out var sw)
                && sw.Sections.Any(other => gchain.Any(scope => ReferenceEquals(scope, other))))
            {
                continue;   // a label RenderSwitch reaches from the goto's section
            }
            return label;
        }
        return null;
    }

    /// <summary>The case section a label is declared at the top level of, from the label's
    /// scope chain: the chain ends at the section, or at the one block that is the section's
    /// whole body (<c>case X: { … }</c>, which RenderSwitch sees through). Null otherwise.</summary>
    private static SwitchSection? SectionOfTopLevelLabel(List<object> lchain) => lchain switch
    {
        [.., SwitchSection sec] => sec,
        [.., SwitchSection sec, Block b] when sec.Body is [var only] && ReferenceEquals(only, b) => sec,
        _ => null,
    };

    /// <summary>One hoist of one label: finds the statement whose if-arm /
    /// nested block declares the label at top level, splits the arm there, and
    /// splices the tail (plus the skip jump) into the parent statement list.</summary>
    private sealed class Hoister
    {
        private readonly string _label;
        private readonly int _step;
        private readonly HashSet<string> _skips;
        private readonly HashSet<Symbol> _renamed;
        public bool Done { get; private set; }

        /// <summary>The declarations a split moves out of its head (see <see cref="HoistDecls"/>),
        /// which go before the split statement in the enclosing list.</summary>
        private readonly List<CStmt> _hoisted = new();

        public Hoister(string label, int step, HashSet<string> skips, HashSet<Symbol> renamed)
        {
            _label = label;
            _step = step;
            _skips = skips;
            _renamed = renamed;
        }

        /// <summary>Move the top-level declarations of a split head out to the enclosing list, since
        /// the tail that leaves the head's block may read them (unicodeobject.c's
        /// <c>{ Py_UCS4 ch; restart: ch = …; }</c>). Each takes a name no other local has, since the
        /// enclosing block may declare the same one; an initializer stays where it ran, as an
        /// assignment. Returns the head's remaining statements.</summary>
        private List<CStmt> HoistDecls(IEnumerable<CStmt> head)
        {
            var kept = new List<CStmt>();
            foreach (var st in head)
            {
                switch (st)
                {
                    case DeclStmt ds:
                        foreach (var d in ds.Decls)
                        {
                            Rename(d.Sym);
                            _hoisted.Add(new DeclStmt(new[] { new LocalDecl(d.Sym, null) }) { MaybeUnused = ds.MaybeUnused, Pos = ds.Pos });
                            if (d.Init is { } init)
                            {
                                var target = new VarRef(d.Sym) { Type = d.Sym.Type, IsLValue = true };
                                kept.Add(new ExprStmt(new Assign(null, target, init) { Type = d.Sym.Type }) { Pos = ds.Pos });
                            }
                        }
                        break;
                    case ArrayDecl ad:
                        Rename(ad.Sym);
                        _hoisted.Add(ad);
                        break;
                    default:
                        kept.Add(st);
                        break;
                }
            }
            return kept;
        }

        /// <summary>Give a hoisted local a name unique in its function, once.</summary>
        private void Rename(Symbol sym)
        {
            if (_renamed.Add(sym)) { sym.TargetName = $"{sym.TargetName}__h{_step}"; }
        }

        public Block Rewrite(Block b) => Done ? b : new Block(RewriteList(b.Stmts)) { Pos = b.Pos };

        /// <summary>One hoist within a statement list (a block's, or a case section's):
        /// flatten a loop, or split an if-arm / nested block / labeled block / switch, whose
        /// top level declares the label; else recurse into the statements.</summary>
        private List<CStmt> RewriteList(IReadOnlyList<CStmt> list)
        {
            var stmts = new List<CStmt>(list);
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
                    return stmts;
                }
                if (TryExtract(stmts[i], out var replaced, out var tail))
                {
                    var skip = _skips.Add($"__skip_{_label}") ? $"__skip_{_label}" : $"__skip{_step}_{_label}";
                    _skips.Add(skip);
                    var insert = new List<CStmt>(_hoisted) { replaced, new Goto(skip) };
                    insert.AddRange(tail);
                    insert.Add(new Labeled(skip, new Block(Array.Empty<CStmt>())));
                    stmts.RemoveAt(i);
                    stmts.InsertRange(i, insert);
                    Done = true;
                    return stmts;
                }
                if (stmts[i] is Switch sw && SplitSwitch(sw, out var head, out var switchTail, out var past))
                {
                    var insert = new List<CStmt>(_hoisted) { head, new Goto(past) };
                    insert.AddRange(switchTail);
                    insert.Add(new Labeled(past, new Block(Array.Empty<CStmt>())));
                    stmts.RemoveAt(i);
                    stmts.InsertRange(i, insert);
                    Done = true;
                    return stmts;
                }
            }
            for (var i = 0; i < stmts.Count && !Done; i++) { stmts[i] = RewriteStmt(stmts[i]); }
            return stmts;
        }

        /// <summary>When a case section of <paramref name="sw"/> declares the label at its top
        /// level (in its body, or in the one block that is its body), and a goto from outside
        /// the switch reaches for it: the switch with that section cut at the label (the head
        /// now ends in <c>goto label;</c>), and the section's tail, which leaves the switch. In
        /// the tail, the section's own <c>break</c>s (not a nested loop's or switch's) become
        /// <c>goto past</c>, the label just after the tail, where leaving the switch lands. A
        /// tail that falls through into the next case cannot leave: that fails loudly.</summary>
        internal bool SplitSwitch(Switch sw, out CStmt head, out List<CStmt> tail, out string past)
        {
            head = sw;
            tail = new List<CStmt>();
            past = "";
            for (var si = 0; si < sw.Sections.Count; si++)
            {
                var sec = sw.Sections[si];
                var wrapped = sec.Body is [Block only] ? only : null;
                var eff = wrapped?.Stmts ?? sec.Body;
                var at = -1;
                for (var k = 0; k < eff.Count; k++)
                {
                    if (eff[k] is Labeled l && l.Name == _label) { at = k; break; }
                }
                if (at < 0) { continue; }
                var leave = _skips.Add($"__sw_leave_{_label}") ? $"__sw_leave_{_label}" : $"__sw_leave{_step}_{_label}";
                _skips.Add(leave);
                past = leave;
                var moved = eff.Skip(at).Select(x => RetargetJumps(x, leave, continueLabel: null)).ToList();
                if (moved.Count == 0 || !EndsFlow(moved[^1]))
                {
                    throw new IrUnsupportedException(
                        $"goto into switch: the case tail at label '{_label}' falls through into the next case, so it cannot leave the switch");
                }
                tail = moved;
                var kept = HoistDecls(eff.Take(at));
                kept.Add(new Goto(_label));
                IReadOnlyList<CStmt> body = wrapped is null ? kept : new List<CStmt> { new Block(kept) { Pos = wrapped.Pos } };
                var sections = sw.Sections.ToList();
                sections[si] = sec with { Body = body };
                for (var other = 0; other < sections.Count; other++)
                {
                    if (other != si) { sections[other] = sections[other] with { Body = HoistDecls(sections[other].Body) }; }
                }
                head = sw with { Sections = sections };
                return true;
            }
            return false;
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
                // A labeled block (ceval's `TARGET_op: { … PRED_op: … }` case body).
                case Labeled lb when lb.Body is Block lbody && SplitArm(lbody, out var lhead, out tail!):
                    replaced = lb with { Body = lhead };
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
                        var headStmts = HoistDecls(ab.Stmts.Take(k));
                        headStmts.Add(new Goto(_label));
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
                Sections = sw.Sections.Select(RewriteSection).ToList(),
            },
            _ => s,
        };

        /// <summary>A case section's body gets the statement-list treatment, so a label nested
        /// in it is hoisted to the section's top level.</summary>
        private SwitchSection RewriteSection(SwitchSection sec)
            => Done ? sec : new SwitchSection(sec.Labels, RewriteList(sec.Body));
    }

    /// <summary>Whether a statement ends control flow: nothing after it in its list runs by
    /// falling off it. A loop does when its body ends in a <c>goto</c> or <c>return</c> (the
    /// <c>do { …; goto exit; } while (0)</c> of a statement macro, sre's RETURN_FAILURE); its
    /// own <c>break</c> and <c>continue</c> only leave the loop.</summary>
    private static bool EndsFlow(CStmt s) => EndsFlow(s, inLoop: false);

    private static bool EndsFlow(CStmt s, bool inLoop) => s switch
    {
        Return or Goto => true,
        Break or Continue => !inLoop,
        Block b => b.Stmts.Count > 0 && EndsFlow(b.Stmts[^1], inLoop),
        Seq q => q.Stmts.Count > 0 && EndsFlow(q.Stmts[^1], inLoop),
        Labeled l => EndsFlow(l.Body, inLoop),
        If { Else: { } el } f => EndsFlow(f.Then, inLoop) && EndsFlow(el, inLoop),
        DoWhile dw => EndsFlow(dw.Body, inLoop: true),
        _ => false,
    };

    /// <summary>
    /// A goto may jump forward past a declaration: the variable exists at the label with
    /// an indeterminate value (pythonrun.c's <c>goto done;</c> before <c>PyObject *dict =
    /// …</c>, with <c>done:</c> reading <c>dict</c> only when it was set). C# requires the
    /// variable definitely assigned there (CS0165), so in each block, a declaration that
    /// follows a statement containing a <c>goto</c> is declared at the top of the block,
    /// defaulted, and its initializer stays where it ran as an assignment. A block whose
    /// declarations all come before its gotos (the usual cleanup shape) is untouched.
    /// </summary>
    private static Block DeclareBeforeGotos(Block body) => (Block)DeclareBeforeGotos((CStmt)body);

    private static CStmt DeclareBeforeGotos(CStmt s) => s switch
    {
        Block b => b with { Stmts = DeclareListBeforeGotos(b.Stmts) },
        Seq q => q with { Stmts = q.Stmts.Select(DeclareBeforeGotos).ToList() },
        Labeled l => l with { Body = DeclareBeforeGotos(l.Body) },
        If f => f with { Then = DeclareBeforeGotos(f.Then), Else = f.Else is { } e ? DeclareBeforeGotos(e) : null },
        While w => w with { Body = DeclareBeforeGotos(w.Body) },
        DoWhile dw => dw with { Body = DeclareBeforeGotos(dw.Body) },
        For fo => fo with { Body = DeclareBeforeGotos(fo.Body) },
        CaseLabelStmt cl => cl with { Body = DeclareBeforeGotos(cl.Body) },
        SetjmpGuard sj => sj with
        {
            TryBody = sj.TryBody is { } tb ? DeclareBeforeGotos(tb) : null,
            CatchBody = sj.CatchBody is { } cb ? DeclareBeforeGotos(cb) : null,
        },
        SetjmpCapture sc => sc with { Body = DeclareBeforeGotos(sc.Body) },
        Switch sw => sw with
        {
            Sections = sw.Sections.Select(sec => sec with { Body = DeclareListBeforeGotos(sec.Body) }).ToList(),
        },
        _ => s,
    };

    /// <summary><see cref="DeclareBeforeGotos(Block)"/> over one statement list. A declaration moves
    /// only to the start of its label-delimited segment (just after the nearest label above it), so
    /// it stays with that label's tail, which RenderSwitch may move out of its switch (ceval's
    /// <c>PREDICTED(op)</c> tail declares the case's locals).</summary>
    private static List<CStmt> DeclareListBeforeGotos(IReadOnlyList<CStmt> list)
    {
        var result = new List<CStmt>();
        var insertAt = 0;
        var afterGoto = false;
        foreach (var st in list)
        {
            if (afterGoto && st is DeclStmt ds)
            {
                foreach (var d in ds.Decls)
                {
                    result.Insert(insertAt++, new DeclStmt(new[] { new LocalDecl(d.Sym, null) }) { MaybeUnused = ds.MaybeUnused, Pos = ds.Pos });
                    if (d.Init is { } init)
                    {
                        var target = new VarRef(d.Sym) { Type = d.Sym.Type, IsLValue = true };
                        result.Add(new ExprStmt(new Assign(null, target, init) { Type = d.Sym.Type }) { Pos = ds.Pos });
                    }
                }
                continue;
            }
            result.Add(DeclareBeforeGotos(st));
            if (st is Labeled) { insertAt = result.Count; }
            afterGoto |= ContainsGoto(st);
        }
        return result;
    }

    /// <summary>Whether a statement contains a <c>goto</c> anywhere.</summary>
    private static bool ContainsGoto(CStmt? s) => s switch
    {
        null => false,
        Goto => true,
        Block b => b.Stmts.Any(ContainsGoto),
        Seq q => q.Stmts.Any(ContainsGoto),
        Labeled l => ContainsGoto(l.Body),
        If f => ContainsGoto(f.Then) || ContainsGoto(f.Else),
        While w => ContainsGoto(w.Body),
        DoWhile dw => ContainsGoto(dw.Body),
        For fo => ContainsGoto(fo.Body),
        CaseLabelStmt cl => ContainsGoto(cl.Body),
        SetjmpGuard sj => ContainsGoto(sj.TryBody) || ContainsGoto(sj.CatchBody),
        SetjmpCapture sc => ContainsGoto(sc.Body),
        Switch sw => sw.Sections.Any(sec => sec.Body.Any(ContainsGoto)),
        _ => false,
    };

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

    /// <summary>Turn a loop body's own <c>break</c> / <c>continue</c> into gotos (a null
    /// <paramref name="continueLabel"/> keeps <c>continue</c>, for a case tail leaving its
    /// switch but not its loop); a nested loop keeps both, a nested switch keeps its
    /// <c>break</c>.</summary>
    private static CStmt RetargetJumps(CStmt s, string breakLabel, string? continueLabel)
    {
        CStmt Walk(CStmt st, bool inSwitch) => st switch
        {
            Break when !inSwitch => new Goto(breakLabel) { Pos = st.Pos },
            Continue when continueLabel is not null => new Goto(continueLabel) { Pos = st.Pos },
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
