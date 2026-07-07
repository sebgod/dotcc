#nullable enable

using DotCC.Frontends;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Always-on unit pins for <see cref="ZigParseProbe"/> — the parse-only engine behind
/// the opt-in std wall-finder (road-to-zig-std.md S0). No zig install needed: these lock
/// the three outcomes the wall-finder buckets on, so the classifier can't silently drift
/// (e.g. start swallowing lex failures as clean parses) even when the probe itself is
/// skipped in CI. The std walk + ranking lives in <c>DotCC.FunctionalTests.StdParseProbeTests</c>.
/// </summary>
public sealed class ZigParseProbeTests
{
    [Fact]
    public void Accepts_a_clean_unit()
    {
        var r = ZigParseProbe.TryParse("pub fn main() u8 { return 0; }\n");
        r.Status.ShouldBe(ZigParseStatus.Ok);
        r.Message.ShouldBeNull();
    }

    [Fact]
    public void Classifies_a_grammar_gap_as_ParseError()
    {
        // A top-level `test` block — one of the biggest std parse gaps. `test` lexes as an
        // ordinary IDENT the top-level grammar has no production for → a parse (not lex) error.
        var r = ZigParseProbe.TryParse("test \"x\" { }\npub fn main() u8 { return 0; }\n");
        r.Status.ShouldBe(ZigParseStatus.ParseError);
        r.Message.ShouldNotBeNull();
    }

    [Fact]
    public void Classifies_an_untokenizable_byte_as_LexError()
    {
        // A `@"quoted"` identifier — the lexer has no rule for `@` followed by `"` (0x40),
        // so it fails before the parser sees a token. 51 std files fail here first.
        var r = ZigParseProbe.TryParse("const x = .@\"io-uring\";\n");
        r.Status.ShouldBe(ZigParseStatus.LexError);
        r.Message.ShouldNotBeNull();
    }
}
