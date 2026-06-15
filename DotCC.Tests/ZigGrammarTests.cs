#nullable enable

using LALR.CC;
using LALR.CC.LexicalGrammar;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Parse-acceptance regression for the production Zig grammar
/// (<c>DotCC.Lib/zig.lalr.yaml</c> → generated <c>DotCC.Zig</c>), behind the
/// <c>IFrontend</c> seam. Slice 1 = the C-shaped value/type core. The grammar
/// generating at all already proves it's LALR(1)-clean (a conflict aborts the
/// build); these tests pin ACCEPTANCE (real Zig parses) and faithful REJECTION
/// (e.g. the non-associative <c>a &lt; b &lt; c</c>) through the real
/// <c>BytesLexer → Parser.ParseInput</c> pipeline with the generated
/// <c>IdentityVisitor</c>. The standalone spike lives in SharpAstro/LALR.CC
/// (<c>examples/Zig</c>); this is the dotcc-side copy the frontend grows on.
/// </summary>
public sealed class ZigGrammarTests
{
    private static bool TryParse(string src)
    {
        try
        {
            var parser = Zig.BuildParser(Zig.IdentityVisitor.Instance);
            using var lexer = BytesLexer.FromString(src, Zig.BuildLexer());
            using var tokens = new SyncLATokenIterator(lexer);
            parser.ParseInput(tokens);
            return true;
        }
        catch (ParseErrorException) { return false; }
    }

    [Theory]
    [InlineData("fn add(a: i32, b: i32) i32 { return a + b * 2; }")]
    [InlineData("const Pi = 3.14;\npub fn main() void {\n    const x = Pi + 1.0;\n    return;\n}")]
    [InlineData("fn f(p: *u8, q: ?T) void {\n    const x = p[0];\n    const y = foo.bar(p, 3) + @intCast(x);\n    var z = a.b.c;\n    z = y;\n    return;\n}")]
    [InlineData("fn g(n: i32) !i32 {\n    var i = 0;\n    while (i < n) {\n        if (i == 3) { return i; } else { i = i + 1; }\n    }\n    return n;\n}")]
    [InlineData("fn h(p: *T) T { return p.*.field.?; }")]
    public void Accepts_real_zig(string src) => TryParse(src).ShouldBeTrue();

    [Theory]
    [InlineData("fn bad(a: i32) i32 { return a < a < a; }")] // compare is non-associative
    [InlineData("fn h() void { + }")]                         // stray operator as a statement
    public void Rejects_invalid(string src) => TryParse(src).ShouldBeFalse();
}
