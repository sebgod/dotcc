#nullable enable

using System.Globalization;
using LALR.CC;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>
/// Rejects a <c>STRAY</c> token (a byte no lexer rule matches, see
/// <see cref="Compiler.LexC"/>) that survives preprocessing, with gcc's wording: a
/// lone quote is <c>missing terminating ' character</c>, anything else
/// <c>stray '@' in program</c>. A stray byte in a skipped <c>#if</c> group, or in a
/// macro body never expanded, never reaches this stage, which is what C11 6.4p1
/// allows. Sits directly after <see cref="MacroExpander"/>.
/// </summary>
internal sealed class StrayTokenGuard : RewritingTokenStream
{
    private readonly string _file;

    /// <summary>Guard the token stream <paramref name="inner"/> of the translation
    /// unit <paramref name="file"/> (named in the diagnostic).</summary>
    public StrayTokenGuard(ISyncIterator<Item> inner, string file) : base(inner) => _file = file;

    /// <inheritdoc/>
    protected override void ProcessToken(Item token)
    {
        if (token.ID == Compiler.StraySymbol)
        {
            var b = StrayByte(token);
            var what = b switch
            {
                (byte)'\'' => "missing terminating ' character",
                (byte)'"' => "missing terminating \" character",
                >= 0x21 and < 0x7F => $"stray '{(char)b}' in program",
                _ => $"stray '\\{System.Convert.ToString(b, 8)}' in program",
            };
            throw new CompileException(
                $"{System.IO.Path.GetFileName(_file)}:{token.Position.Line}:{token.Position.Column}: error: {what}");
        }
        Emit(token);
    }

    /// <summary>The byte a <c>STRAY</c> token stands for: the lexer's error item
    /// carries it as <c>\xNN</c>.</summary>
    internal static byte StrayByte(Item token) =>
        token.Content is string { Length: 4 } s && s.StartsWith("\\x", System.StringComparison.Ordinal)
            ? byte.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : (byte)'?';
}
