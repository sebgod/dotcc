#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using LALR.CC;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>What one <c>#pragma pack</c> directive does to the packing state: set the
/// current alignment cap, push the current one (then optionally set), or pop.</summary>
internal enum PackOp
{
    /// <summary><c>#pragma pack(N)</c>, or <c>#pragma pack()</c> back to natural alignment.</summary>
    Set,
    /// <summary><c>#pragma pack(push)</c> / <c>(push, N)</c> / <c>(push, label)</c> / <c>(push, label, N)</c>.</summary>
    Push,
    /// <summary><c>#pragma pack(pop)</c> / <c>(pop, N)</c> / <c>(pop, label)</c>.</summary>
    Pop,
}

/// <summary>
/// One <c>#pragma pack</c> directive, the content of the synthetic carrier token
/// <see cref="CPreprocessor.OnPragma"/> puts in the token stream where the directive
/// stood. <see cref="PragmaPackRewriter"/> consumes it; the <c>-E</c> path, which does
/// not run the rewriter, prints it back as the directive on a line of its own.
/// </summary>
/// <param name="Op">What the directive does.</param>
/// <param name="Label">The identifier of a <c>push</c> or <c>pop</c>, if any.</param>
/// <param name="Value">The alignment cap in bytes (a power of two up to 16), 0 for
/// natural alignment, or null where the directive names none.</param>
internal sealed record PackPragma(PackOp Op, string? Label, int? Value)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        var args = new List<string>();
        if (Op != PackOp.Set) { args.Add(Op == PackOp.Push ? "push" : "pop"); }
        if (Label is { } l) { args.Add(l); }
        if (Value is { } v and > 0) { args.Add(v.ToString(CultureInfo.InvariantCulture)); }
        return "\n#pragma pack(" + string.Join(", ", args) + ")\n";
    }
}

/// <summary>
/// The content of a <c>struct</c> or <c>union</c> keyword token met while a
/// <c>#pragma pack</c> cap was in force: the keyword's spelling and the cap, which the
/// binder reads off a definition's keyword (<c>IrBuilder.DefineAggregate</c>). Only a
/// capped keyword carries one; every other keyword keeps its plain string content.
/// </summary>
/// <param name="Spelling">The keyword, <c>struct</c> or <c>union</c>.</param>
/// <param name="Pack">The alignment cap in bytes (1, 2, 4, 8 or 16).</param>
internal sealed record PackedTagKeyword(string Spelling, int Pack)
{
    /// <summary>The keyword as it stands in the source, for diagnostics.</summary>
    public override string ToString() => Spelling;

    /// <summary>The text a parse fingerprint takes for the keyword: two definitions of
    /// one tag under different caps are different types.</summary>
    public string FingerprintText => Spelling + "@pack" + Pack.ToString(CultureInfo.InvariantCulture);

    /// <summary>The cap a <c>struct</c>/<c>union</c> keyword token carries: 0 (natural
    /// alignment) unless a <c>#pragma pack</c> stamped it.</summary>
    public static int Of(Item keyword) => keyword.Content is PackedTagKeyword k ? k.Pack : 0;
}

/// <summary>
/// Applies <c>#pragma pack</c> in token order. Sits directly after
/// <see cref="MacroExpander"/>, so a <c>struct</c> a macro expands to is capped by the
/// directives before its use: it consumes each <see cref="PackPragma"/> carrier, keeps
/// the current cap and the push stack (one translation unit's, starting at natural
/// alignment as gcc, clang and MSVC do), and stamps every <c>struct</c> and
/// <c>union</c> keyword met under a cap with a <see cref="PackedTagKeyword"/>.
/// </summary>
internal sealed class PragmaPackRewriter : RewritingTokenStream
{
    private readonly int _pragmaSymbol, _structSymbol, _unionSymbol;
    private readonly List<(string? Label, int Pack)> _stack = new();
    private int _current;

    /// <summary>Rewrite the token stream <paramref name="inner"/>.</summary>
    public PragmaPackRewriter(ISyncIterator<Item> inner) : base(inner)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sym in C.Definition.SymbolNames) { map[sym.Name] = sym.ID; }
        _pragmaSymbol = map["#pragma"];
        _structSymbol = map["struct"];
        _unionSymbol = map["union"];
    }

    /// <inheritdoc/>
    protected override void ProcessToken(Item token)
    {
        if (token.ID == _pragmaSymbol && token.Content is PackPragma p)
        {
            Apply(p);
            return;
        }
        if (_current > 0 && (token.ID == _structSymbol || token.ID == _unionSymbol) && token.Content is string spelling)
        {
            Emit(new Item(token.ID, new PackedTagKeyword(spelling, _current), token.Position));
            return;
        }
        Emit(token);
    }

    /// <summary>Apply one directive. A <c>pop</c> with a label unwinds to the newest
    /// entry pushed under it and removes that one too, as MSVC and gcc do; a pop with
    /// nothing to pop leaves the cap as it is (gcc warns and does the same).</summary>
    private void Apply(PackPragma p)
    {
        switch (p.Op)
        {
            case PackOp.Set:
                _current = p.Value ?? 0;
                break;
            case PackOp.Push:
                _stack.Add((p.Label, _current));
                if (p.Value is { } pushed) { _current = pushed; }
                break;
            case PackOp.Pop:
                var at = _stack.Count - 1;
                if (p.Label is { } label)
                {
                    while (at >= 0 && _stack[at].Label != label) { at--; }
                }
                if (at >= 0)
                {
                    _current = _stack[at].Pack;
                    _stack.RemoveRange(at, _stack.Count - at);
                }
                if (p.Value is { } popped) { _current = popped; }
                break;
        }
    }
}
