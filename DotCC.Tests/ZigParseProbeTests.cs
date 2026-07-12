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
    public void Accepts_a_test_block()
    {
        // A top-level `test` block used to be one of the biggest std parse gaps; road-to-zig-std S9
        // now parses (and drops) it. This pins that it lexes AND parses clean — the win the probe
        // should count, not a gap. (Was a ParseError pin before the `test`/comptime brick landed.)
        var r = ZigParseProbe.TryParse("test \"x\" { }\npub fn main() u8 { return 0; }\n");
        r.Status.ShouldBe(ZigParseStatus.Ok);
        r.Message.ShouldBeNull();
    }

    [Fact]
    public void Accepts_a_quoted_identifier()
    {
        // A `@"quoted"` identifier — Zig's reserved-word / arbitrary-string escape hatch. The lexer
        // now has a rule for `@` followed by `"` (road-to-zig-std S9), so `@"a-b"` lexes as an IDENT
        // and parses clean. (Was a LexError pin before the quoted-identifier brick landed.)
        var r = ZigParseProbe.TryParse("pub fn main() u8 { const @\"a-b\": u8 = 42; return @\"a-b\"; }\n");
        r.Status.ShouldBe(ZigParseStatus.Ok);
        r.Message.ShouldBeNull();
    }

    [Fact]
    public void Accepts_concat_and_repeat_operators()
    {
        // `++` (array/string concat) and `**` (array repeat) — road-to-zig-std S9. Both now lex as
        // distinct tokens (not `+`/`*`) and parse at their Zig precedence (Add / Mul). Parse-only:
        // lowering is deferred, but the probe counts the parse.
        var concat = ZigParseProbe.TryParse("const s = \"a\" ++ \"b\";\n");
        concat.Status.ShouldBe(ZigParseStatus.Ok);
        var repeat = ZigParseProbe.TryParse("const a = base ** 4;\n");
        repeat.Status.ShouldBe(ZigParseStatus.Ok);
    }

    [Fact]
    public void Classifies_a_grammar_gap_as_ParseError()
    {
        // An incomplete decl — `const x =` with no initializer expression. Every token lexes, but the
        // grammar has no production for an empty RHS, so the parser rejects it → a parse (not lex)
        // error. Permanently invalid Zig, so this stays a stable ParseError pin as the grammar grows.
        var r = ZigParseProbe.TryParse("const x = ;\n");
        r.Status.ShouldBe(ZigParseStatus.ParseError);
        r.Message.ShouldNotBeNull();
    }

    [Fact]
    public void Classifies_an_untokenizable_byte_as_LexError()
    {
        // `$` (0x24) has no lexer rule and is never used in Zig, so the lexer fails before the parser
        // sees a token. A stable LexError pin — unlike `@` (now a quoted-identifier lead), `$` will
        // never become tokenizable.
        var r = ZigParseProbe.TryParse("const x = $;\n");
        r.Status.ShouldBe(ZigParseStatus.LexError);
        r.Message.ShouldNotBeNull();
    }
}
