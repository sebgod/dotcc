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
    public void Accepts_nested_container_decls_as_container_members()
    {
        // A NESTED `const Inner = struct {…};` (also enum/union) inside a container body — road-to-zig-std
        // S9, the top `:` parse bucket (41 std files). Container-body member lists now admit a
        // ContainerDecl, so a container can hold its own nested named-field types (fields, methods, and
        // nested types compose). The headline shape is the tagged-union-with-nested-struct in
        // std/Build/Step/Compile.zig. Parse-only: lowering a nested container is still a loud cut.
        var inStruct = ZigParseProbe.TryParse(
            "const Outer = struct { x: u8, pub const Inner = struct { y: u8, pub fn f() void {} }; };\n");
        inStruct.Status.ShouldBe(ZigParseStatus.Ok);
        var inUnion = ZigParseProbe.TryParse(
            "const U = union(enum) { file: File, pub const File = struct { source: u8 }; };\n");
        inUnion.Status.ShouldBe(ZigParseStatus.Ok);
        var enumInStruct = ZigParseProbe.TryParse(
            "const Outer = struct { x: u8, const Kind = enum { a, b }; };\n");
        enumInStruct.Status.ShouldBe(ZigParseStatus.Ok);
    }

    [Fact]
    public void Accepts_return_and_value_bodies_in_switch_prongs()
    {
        // A switch prong whose body is `return [e]` or (with a payload capture) a bare value expression —
        // road-to-zig-std S9, the `return` parse bucket + its capture sibling. The prong body is now
        // symmetric: a Block, a `return [e]`, or any RhsExpr, both with and without a `|x|`/`|*x|` capture.
        // Parse-only: the switch-prong lowering doesn't yet bind these bodies.
        var ret = ZigParseProbe.TryParse(
            "fn f(m: u8) u8 { switch (m) { 0 => return 1, else => return 2 } }\n");
        ret.Status.ShouldBe(ZigParseStatus.Ok);
        var retVoid = ZigParseProbe.TryParse(
            "fn f(m: u8) void { switch (m) { 0 => return, else => return } }\n");
        retVoid.Status.ShouldBe(ZigParseStatus.Ok);
        var captureReturn = ZigParseProbe.TryParse(
            "fn f(s: U) u8 { switch (s) { .a, .b => |v| return v, else => |v| return v } }\n");
        captureReturn.Status.ShouldBe(ZigParseStatus.Ok);
    }

    [Fact]
    public void Accepts_inline_named_field_struct_types_in_annotation_positions()
    {
        // An inline named-field struct type `struct { a: u8, … }` in a TYPE-annotation slot — road-to-zig-std
        // S9, the last of the `:`-in-307 bucket. Parses as a return type, a struct-field type, a param type,
        // a `: T` var/const annotation, and a union-variant payload (the `AType` fork). Deliberately NOT in
        // value position: `const X = struct {…}` stays a container decl, so those two never collide.
        var ret = ZigParseProbe.TryParse("fn f() struct { a: u8 } { return .{ .a = 0 }; }\n");
        ret.Status.ShouldBe(ZigParseStatus.Ok);
        var field = ZigParseProbe.TryParse("const S = struct { inner: struct { a: u8, pub fn g() void {} } };\n");
        field.Status.ShouldBe(ZigParseStatus.Ok);
        var param = ZigParseProbe.TryParse("fn f(p: struct { a: u8 }) void { _ = p; }\n");
        param.Status.ShouldBe(ZigParseStatus.Ok);
        var payload = ZigParseProbe.TryParse("const U = union(enum) { one: struct { a: u8 }, two };\n");
        payload.Status.ShouldBe(ZigParseStatus.Ok);
        // Invariant: a value-position named struct stays a container decl (structDecl), unaffected.
        var value = ZigParseProbe.TryParse("const X = struct { a: u8 };\n");
        value.Status.ShouldBe(ZigParseStatus.Ok);
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
