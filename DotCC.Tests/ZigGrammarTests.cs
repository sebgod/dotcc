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
[Collection("ZigGrammar")]
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
    // slice 2a — if-expression in value position (init / return / assignment RHS)
    [InlineData("fn f(c: bool) i32 { const x = if (c) 1 else 2; return x; }")]
    [InlineData("fn g(c: bool) i32 { return if (c) 10 else 20; }")]
    [InlineData("fn k(c: bool) i32 { var x = 0; x = if (c) 1 else 2; return x; }")]
    [InlineData("fn n(a: bool, b: bool) i32 { return if (a) 1 else if (b) 2 else 3; }")] // nested
    [InlineData("fn m(c: bool) i32 { return if (c) 1 else 2 + 3; }")] // greedy else: else = (2 + 3)
    // GH #129: an `if` as the right operand of `orelse` / `catch`, without parentheses
    [InlineData("fn f(o: ?u8, g: bool) u8 { return o orelse if (g) 5 else 6; }")]
    [InlineData("fn f(o: E!u8, g: bool) u8 { return o catch if (g) 5 else 6; }")]
    [InlineData("fn f(o: ?u8, g: bool) u8 { const v: u8 = o orelse if (g) 5 else return 6; return v; }")]
    [InlineData("fn f(o: ?u8, p: ?u8, g: bool) u8 { return o orelse if (g) 5 else p orelse 6; }")]
    [InlineData("const m: usize = a orelse (b orelse if (c) @compileError(\"x\") else @compileError(\"y\"));")]
    [InlineData("fn f(o: ?u8, p: E!u8) S { return .{ .n = o orelse if (p) |n| n + 1 else |_| 0 }; }")]
    // GH #282: a tagged enum, with methods, as a type arm
    [InlineData("const P = if (w) enum(u32) { a = 1, _ } else enum(u16) { b = 2, pub fn f(p: @This()) u16 { return @intFromEnum(p); } };")]
    // GH #283: jumps as arms of a value `if`, and an `if` whose arms both jump as an `orelse` fallback
    [InlineData("fn f(xs: []const u8) u8 { var t: u8 = 0; for (xs) |x| { const y = if (x < 100) x else break; t += y; } return t; }")]
    [InlineData("fn f(xs: []const u8) u8 { var t: u8 = 0; for (xs) |x| { t += if (x == 0) continue else x; } return t; }")]
    [InlineData("fn f(o: ?u8, c: bool) !?u8 { const v: ?u8 = blk: { const q = o orelse if (c) return error.U else break :blk null; break :blk q + 1; }; return v; }")]
    [InlineData("fn f(o: ?u8, d: u8) u8 { const v = blk: { const q = o orelse break :blk d; break :blk q + 1; }; return v; }")]
    [InlineData("fn f(xs: []const u8, d: u8) u8 { const v = blk: { for (xs) |x| { const y = if (x < 9) x else break :blk d; _ = y; } break :blk 0; }; return v; }")]
    // GH #285: enum members named after primitive values, with a value, and qualified access to them
    [InlineData("const P = enum(u8) { null = 0, all_ones = 255, _ };")]
    [InlineData("const V = enum(u8) { none = 0, false = 1 + @intFromEnum(C.false), true = 2, _ };")]
    [InlineData("const R = enum(u8) { undefined = 7, same_value };")]
    [InlineData("const R = enum { undefined, other };")]
    [InlineData("const t = Constant.true; const n = E.null; const u = E.undefined;")]
    // GH #286: a labeled switch's `continue :label operand`, as a statement, a prong body, a value `if` arm and a fallback
    [InlineData("fn f(s: State) void { state: switch (s) { .a => { continue :state .b; }, .b => {} } }")]
    [InlineData("fn f(s: State) u8 { return sw: switch (s) { .a => continue :sw .b, .b => 1 }; }")]
    [InlineData("fn f(s: State, v: u8) u8 { return r: switch (s) { .a => if (v < 5) 0 else continue :r .b, .b => 1 }; }")]
    [InlineData("fn f(s: State, o: ?State) void { sw: switch (s) { .a => { const n = o orelse continue :sw .b; _ = n; }, .b => {} } }")]
    public void Accepts_real_zig(string src) => TryParse(src).ShouldBeTrue();

    [Theory]
    [InlineData("fn bad(a: i32) i32 { return a < a < a; }")] // compare is non-associative
    [InlineData("fn h() void { + }")]                         // stray operator as a statement
    public void Rejects_invalid(string src) => TryParse(src).ShouldBeFalse();

    /// <summary>
    /// Documented slice-2a limitation (NOT a faithful rejection — Zig accepts this):
    /// an if-expression is only reachable in value position (init/return/assignment
    /// RHS and recursively its branches), not as an arbitrary binary sub-operand. So
    /// <c>1 + if (c) 2 else 3</c> currently fails to parse; in Zig it's legal (the
    /// if-expr is the right operand, with <c>!ExprSuffix</c> stopping it being
    /// extended). Lifting this — letting an if-expr be a parenthesized/sub-operand
    /// expression — is slice 2b. This test pins the current boundary so we notice
    /// when it moves.
    /// </summary>
    [Fact]
    public void Slice2a_if_expr_not_yet_a_binary_suboperand() =>
        TryParse("fn bad(c: bool) i32 { return 1 + if (c) 2 else 3; }").ShouldBeFalse();
}
