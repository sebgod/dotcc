#nullable enable

using System;
using DotCC.Ir;
using LALR.CC;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>
/// How a single <see cref="ZigParseProbe.TryParse"/> attempt ended. A parse-only
/// classification — it never lowers, so the only outcomes are "the grammar
/// accepted it", "the lexer choked on a byte", "the parser hit a syntax it can't
/// reduce", or an unexpected internal throw.
/// </summary>
internal enum ZigParseStatus
{
    /// <summary>The generated Zig grammar accepted the whole unit (a clean parse tree).</summary>
    Ok,
    /// <summary>A <see cref="ParseErrorException"/> (or an error <see cref="Item"/>) —
    /// a token the grammar has no production for in the current state.</summary>
    ParseError,
    /// <summary>A <see cref="LexerException"/> — a byte the lexer can't tokenize at all
    /// (e.g. <c>@</c> in a <c>@"quoted"</c> identifier the lexer doesn't yet model).</summary>
    LexError,
    /// <summary>Any other throw while parsing (defensive — the walker must survive
    /// the whole of std, so a stack overflow / internal error on one file is a bucket,
    /// not a crash).</summary>
    OtherError,
}

/// <summary>The outcome of parsing one Zig unit plus the raw diagnostic message
/// (null on <see cref="ZigParseStatus.Ok"/>). The caller normalizes
/// <see cref="Message"/> into a stable bucket key.</summary>
internal readonly record struct ZigParseProbeResult(ZigParseStatus Status, string? Message);

/// <summary>
/// Parse-ONLY driver for the Zig grammar — the engine behind the wall-finder
/// (road-to-zig-std.md, milestone S0). It runs exactly the lex → parse sequence
/// <see cref="ZigFrontend.AddUnits"/> uses, but stops before <c>ZigLowering</c>:
/// the question S0 asks is purely "does the grammar <i>accept</i> this file?",
/// so lowering (which would fail on a hundred not-yet-built constructs and
/// conflate parse gaps with lowering gaps) is deliberately not run.
/// <para>Pure and AOT-clean — no I/O, no <c>Process.Start</c>. The opt-in test that
/// walks the pinned <c>lib/std</c> and ranks the failures lives in the functional
/// test project (<c>StdParseProbeTests</c>), which owns the file walk and the
/// toolchain-locating <c>zig env</c> spawn.</para>
/// </summary>
internal static class ZigParseProbe
{
    /// <summary>Lex + parse <paramref name="source"/> with the generated Zig grammar and
    /// classify the result. Never throws — every failure mode is folded into a
    /// <see cref="ZigParseProbeResult"/> so a caller walking hundreds of files never
    /// has to guard the call.</summary>
    public static ZigParseProbeResult TryParse(string source)
    {
        try
        {
            var parser = Zig.BuildParser(Zig.IdentityVisitor.Instance);
            using var lexer = BytesLexer.FromString(source, Zig.BuildLexer());
            using var tokens = new SyncLATokenIterator(lexer);
            var root = parser.ParseInput(tokens, debugger: null, trimReductions: true);
            return root.IsError
                ? new ZigParseProbeResult(ZigParseStatus.ParseError, root.ToString())
                : new ZigParseProbeResult(ZigParseStatus.Ok, null);
        }
        catch (ParseErrorException ex)
        {
            return new ZigParseProbeResult(ZigParseStatus.ParseError, ex.Message);
        }
        catch (LexerException ex)
        {
            return new ZigParseProbeResult(ZigParseStatus.LexError, ex.Message);
        }
        catch (Exception ex)
        {
            return new ZigParseProbeResult(ZigParseStatus.OtherError, ex.GetType().Name + ": " + ex.Message);
        }
    }
}
