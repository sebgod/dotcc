#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using LALR.CC;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>
/// A token that an <c>#include</c> has already run through the preprocessor
/// and <see cref="MacroExpander"/> of the included file. Macro expansion must
/// happen exactly once, in source order, against the macro table as it stands
/// at that point; the includer's expander therefore passes these through
/// untouched. Rescanning them there would apply a macro that the header
/// <c>#define</c>s AFTER the tokens (<c>int Py_Is(PyObject *x, …);</c>
/// followed by <c>#define Py_Is(x, y) …</c>) and would expand again a
/// self-referential call the included file's rescan had already painted blue.
/// </summary>
internal sealed class ExpandedItem : Item
{
    /// <summary>Copy <paramref name="token"/> (symbol, content, position) as an
    /// already-expanded token.</summary>
    public ExpandedItem(Item token) : base(token.ID, token.Content, token.Position) { }
}

/// <summary>
/// The macro-replacement stage of the pipeline, between
/// <see cref="PreprocessorTokenStream"/> (which runs the directives and hands
/// ordinary text through untouched) and <see cref="DialectKeywordRewriter"/>.
/// Every token goes through the shared <see cref="MacroEngine"/>, which replaces
/// object-like and function-like macros alike and reads further tokens from
/// upstream only when a function-like macro name needs its <c>(</c> and
/// arguments.
/// </summary>
internal sealed class MacroExpander : RewritingTokenStream
{
    private readonly MacroEngine _engine;
    private readonly MacroEngine.Source _source;
    private readonly Action<Item> _emit;

    /// <summary>Replace macros in <paramref name="inner"/> against
    /// <paramref name="cpp"/>'s macro table.</summary>
    public MacroExpander(ISyncIterator<Item> inner, CPreprocessor cpp) : base(inner)
    {
        _engine = cpp.Macros;
        _source = new MacroEngine.Source(ReadUpstream);
        _emit = Emit;
    }

    /// <summary>The engine's upstream reader: the next token of the inner stream.</summary>
    private bool ReadUpstream([NotNullWhen(true)] out Item? item)
    {
        if (TryReadNext(out var next))
        {
            item = next;
            return true;
        }
        item = null;
        return false;
    }

    /// <inheritdoc/>
    protected override void ProcessToken(Item token)
    {
        // An included file's tokens were expanded by that file's own expander,
        // in order, while its #defines were live. Never again here.
        if (token is ExpandedItem)
        {
            Emit(token);
            return;
        }
        _engine.Run(token, _source, _emit);
    }
}
