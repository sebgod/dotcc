#nullable enable

using System;
using System.Collections.Generic;
using LALR.CC;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>
/// A position-free structural fingerprint of parse-tree nodes, computed as the parser reduces them: the production and
/// its children's fingerprints, where a token's is its symbol and text. Two subtrees have the same fingerprint when they
/// hold the same tokens in the same shape, which is how the C binder recognises a header's definition re-included by
/// another translation unit.
/// </summary>
/// <remarks>
/// <para>It replaces the parse tree's <c>ToString()</c> as that fingerprint. The generated records print recursively,
/// each level copying its whole subtree's text, so a long initializer list (a left-recursive cons list, as deep as it is
/// long) cost time quadratic in its length and a stack as deep: CPython's frozen modules, a megabyte of byte arrays,
/// took minutes. Here each node costs O(1) at its reduction.</para>
/// <para>No table maps every node to its fingerprint, since one entry per node costs more than the rest of the parse.
/// An LR parser builds the tree bottom up and left to right, so a reduction's nonterminal children are always the most
/// recent nodes built and not yet consumed, in order: their fingerprints are the top of a stack that mirrors the
/// parser's. Only the nodes the consumer will ask about (<c>keep</c>) are remembered past their parent's
/// reduction.</para>
/// </remarks>
internal sealed class ParseFingerprints
{
    /// <summary>A 128-bit fingerprint (two independently mixed 64-bit lanes).</summary>
    internal readonly record struct Fp(ulong A, ulong B);

    private readonly Func<object, bool> _keep;
    private readonly List<Fp> _pending = new();
    private readonly Dictionary<object, Fp> _kept = new(ReferenceEqualityComparer.Instance);
    private bool[] _nonterminal = [];

    /// <summary>Fingerprints for a parse, remembering those of the nodes <paramref name="keep"/> selects.</summary>
    public ParseFingerprints(Func<object, bool> keep) => _keep = keep;

    /// <summary>
    /// A parser for the same grammar and tables as <paramref name="parser"/> whose every reduction also computes the
    /// fingerprint of the node it builds. It must be run trimming single-nonterminal reductions, as the C front end
    /// does: such a node is its child, with the child's fingerprint.
    /// </summary>
    public Parser Wrap(Parser parser)
    {
        var grammar = parser.Grammar;
        _nonterminal = new bool[grammar.SymbolNames.Length];
        foreach (var group in grammar.PrecedenceGroups)
        {
            foreach (var p in group.Productions) { _nonterminal[p.Left] = true; }
        }
        var groups = new PrecedenceGroup[grammar.PrecedenceGroups.Length];
        var index = 0;
        for (var gi = 0; gi < groups.Length; gi++)
        {
            var src = grammar.PrecedenceGroups[gi];
            var productions = new List<Production>();
            foreach (var p in src.Productions)
            {
                // A trimmed production (one nonterminal, no action) builds no node: it keeps its child, and so its
                // child's fingerprint. Giving it an action would stop the trimming.
                var trimmed = !p.HasRewriter && p.Right.Length == 1 && _nonterminal[p.Right[0]];
                productions.Add(trimmed ? p : Recording(p, index));
                index++;
            }
            groups[gi] = new PrecedenceGroup(src.Derivation, productions.ToArray());
        }
        return new Parser(new Grammar(grammar.SymbolNames, groups), parser.ParseTable);
    }

    /// <summary>
    /// <paramref name="p"/> (production number <paramref name="production"/>) with its action wrapped to fingerprint the
    /// node it builds. A production with no action builds what the parser builds for one, a <see cref="Reduction"/>.
    /// </summary>
    private Production Recording(Production p, int production) =>
        new(p.Left, (_, children) =>
        {
            var fp = Reduce((ulong)production, children);
            var node = p.HasRewriter ? p.Rewrite(children) : new Reduction(production, children);
            _pending.Add(fp);
            if (node is not null && _keep(node)) { _kept[node] = fp; }
            return node;
        }, p.Right);

    /// <summary>
    /// The fingerprint of a node of <paramref name="production"/> over <paramref name="children"/>: a token child
    /// contributes its symbol and text, a nonterminal child the pending fingerprint it left, which this consumes. The
    /// children are taken right to left, so each nonterminal's is the top of the stack.
    /// </summary>
    private Fp Reduce(ulong production, Item[] children)
    {
        var a = production * 0x9E3779B97F4A7C15UL;
        var b = ~production;
        for (var i = children.Length - 1; i >= 0; i--)
        {
            var child = children[i];
            Fp c;
            if (_nonterminal[child.ID])
            {
                if (_pending.Count == 0)
                {
                    throw new InvalidOperationException(
                        "parse fingerprints are out of step with the parser (a reduction's child was not built by it)");
                }
                c = _pending[^1];
                _pending.RemoveAt(_pending.Count - 1);
            }
            else
            {
                c = Leaf(child.ID, child.Content as string ?? "");
            }
            // The children's fingerprints are already mixed, so folding them in takes one multiply per lane.
            a = (a ^ c.A) * 0x100000001B3UL;
            b = (b + c.B) * 0xC2B2AE3D27D4EB4FUL;
        }
        return new Fp(Mix(a), Mix(b ^ (ulong)children.Length));
    }

    /// <summary>The fingerprint of <paramref name="item"/>, a node <c>keep</c> selected.</summary>
    public Fp Of(Item item) => item.Content is { } node && _kept.TryGetValue(node, out var fp)
        ? fp
        : throw new InvalidOperationException(
            $"no parse fingerprint kept for a {item.Content?.GetType().Name ?? "null"} node (not selected, or not built by the wrapped parser)");

    /// <summary>
    /// Forget the parse so far. The kept nodes stay alive while remembered, so this runs once a translation unit is
    /// bound, when its fingerprints are held by value and its tree is garbage.
    /// </summary>
    public void Clear()
    {
        _pending.Clear();
        _kept.Clear();
    }

    /// <summary>A token's fingerprint: its symbol and text (FNV-1a in two seeds).</summary>
    private static Fp Leaf(int symbol, string text)
    {
        var a = 0xCBF29CE484222325UL ^ (ulong)symbol;
        var b = 0x84222325CBF29CE4UL + (ulong)symbol;
        foreach (var ch in text)
        {
            a = (a ^ ch) * 0x100000001B3UL;
            b = (b ^ ch) * 0x100000001B3UL + 1;
        }
        return new Fp(Mix(a), Mix(b ^ (ulong)text.Length));
    }

    /// <summary>SplitMix64's finalizer: every input bit reaches every output bit.</summary>
    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
