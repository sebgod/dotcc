#nullable enable

using LALR.CC;
using LALR.CC.LexicalGrammar;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Regression: the Zig parse table must accept a typed struct literal <c>Point{ … }</c>
/// (Zig's <c>CurlySuffixExpr</c>). Adding the <c>CurlySuffix</c> grammar productions once
/// looked "broken" only because a <c>--no-build</c> run executed a stale binary whose
/// generated table predated the grammar change. These assert both the generated (pre-baked)
/// table AND a fresh rebuild of it parse the construct — guarding against a stale or
/// divergent generated parse table.
/// </summary>
public class ZigParseTableRegressionTests
{
    private const string TypedStructLiteral =
        "const Point = struct { x: u8 };\npub fn main() u8 { const p = Point{ .x = 5 }; return p.x; }\n";

    [Fact]
    public void GeneratedTable_ParsesTypedStructLiteral()
        => AssertParses(DotCC.Zig.BuildParser(DotCC.Zig.IdentityVisitor.Instance));

    [Fact]
    public void RebuiltTable_ParsesTypedStructLiteral()
        => AssertParses(new Parser(DotCC.Zig.Definition));

    private static void AssertParses(Parser parser)
    {
        using var lexer = BytesLexer.FromString(TypedStructLiteral, DotCC.Zig.BuildLexer());
        using var tokens = new SyncLATokenIterator(lexer);
        var root = parser.ParseInput(tokens, debugger: null, errorMode: ParserErrorMode.Return, trimReductions: true);
        Assert.False(root.IsError, $"failed to parse a typed struct literal: {root}");
    }
}
