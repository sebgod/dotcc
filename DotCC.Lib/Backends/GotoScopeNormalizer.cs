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
/// fall-through reaches <c>rest</c> exactly as block-exit did. Only if-arms and
/// plain nested blocks are hoisted through — a label needing to move out of a
/// LOOP or SWITCH body changes iteration semantics, and fails loudly instead
/// (the switch-internal cases are RenderSwitch's own hoisting machinery).
/// Functions with no scope violation pass through untouched (the common case —
/// the pass costs one read-only scan).
/// </remarks>
internal static class GotoScopeNormalizer
{
    public static Block Normalize(Block body)
    {
        // Each hoist lifts the label one block level; a C function body has
        // bounded nesting, so this converges fast — the guard is a backstop.
        for (var guard = 0; guard < 64; guard++)
        {
            var label = FindViolation(body);
            if (label == null) { return body; }
            var h = new Hoister(label);
            body = h.Rewrite(body);
            if (!h.Done)
            {
                throw new IrUnsupportedException(
                    $"goto into a block the normalizer cannot hoist through (label '{label}' — a loop/switch body, or an unhandled nesting shape)");
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
        public bool Done { get; private set; }

        public Hoister(string label) => _label = label;

        public Block Rewrite(Block b)
        {
            if (Done) { return b; }
            var stmts = new List<CStmt>(b.Stmts);
            for (var i = 0; i < stmts.Count; i++)
            {
                if (TryExtract(stmts[i], out var replaced, out var tail))
                {
                    var skip = $"__skip_{_label}";
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
}
