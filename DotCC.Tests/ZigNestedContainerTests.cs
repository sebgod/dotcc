#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for NESTED containers as full containers (road-to-zig-std G3 — the wall
/// <c>std.fmt.Number</c> raised: <c>mode: Mode = .decimal</c> names a nested <c>pub const Mode = enum {…}</c>
/// with a method). Pass 0 now registers every nested container member as an ordinary container under a
/// parent-mangled name (<c>Number__Mode</c>) — so any KIND nests (struct / enum / union), with methods,
/// consts and deeper nesting — and the plain name resolves through the lexical parent chain while any of
/// the parent's members is in scope: a sibling FIELD's type (which needed the parent's field layout to be
/// registered with the parent in scope), a method, a grandchild. <c>Parent.Inner</c> resolves qualified in
/// type and value position. End-to-end in the <c>nested_containers</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigNestedContainerTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zignest-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_field_typed_by_a_nested_enum_declared_after_it_resolves()
    {
        // std.fmt.Number verbatim in shape: the field comes first, the nested enum (with a method) after.
        var cs = EmitZig("""
            const Number = struct {
                mode: Mode = .decimal,
                width: ?usize = null,

                pub const Mode = enum {
                    decimal,
                    hex,

                    pub fn base(mode: Mode) u8 {
                        return switch (mode) {
                            .decimal => 10,
                            .hex => 16,
                        };
                    }
                };
            };
            pub fn main() u8 {
                const n: Number = .{ .mode = .hex };
                return n.mode.base() + 26;
            }
            """);
        cs.ShouldContain("enum Number__Mode");
        cs.ShouldContain("public Number__Mode mode;");
        cs.ShouldContain("Number__Mode_base(");
    }

    [Fact]
    public void A_field_typed_by_a_nested_struct_resolves()
    {
        var cs = EmitZig("""
            const Outer = struct {
                p: Point,
                pub const Point = struct { x: u8, y: u8 };
            };
            pub fn main() u8 {
                const o: Outer = .{ .p = .{ .x = 40, .y = 2 } };
                return o.p.x + o.p.y;
            }
            """);
        cs.ShouldContain("struct Outer__Point");
        cs.ShouldContain("public Outer__Point p;");
    }

    [Fact]
    public void A_nested_type_is_reachable_qualified_in_type_and_value_position()
    {
        var cs = EmitZig("""
            const N = struct {
                mode: Mode,
                pub const Mode = enum { a, b };
                pub const P = struct {
                    pub const K: u8 = 40;
                    pub fn two() u8 { return 2; }
                };
            };
            pub fn main() u8 {
                const m: N.Mode = N.Mode.b;
                return if (m == .b) N.P.K + N.P.two() else 1;
            }
            """);
        cs.ShouldContain("N__Mode m = N__Mode.b");
        cs.ShouldContain("N__P_two(");
    }

    [Fact]
    public void A_grandchild_names_an_uncle_by_its_plain_name()
    {
        // Lexical scoping: `B`'s field names `K`, declared in `A` — one level out.
        var cs = EmitZig("""
            const A = struct {
                b: B,
                pub const K = enum { x, y };
                pub const B = struct {
                    k: K,
                    c: C,
                    pub const C = struct { v: u8 };
                };
            };
            pub fn main() u8 {
                const a: A = .{ .b = .{ .k = .y, .c = .{ .v = 42 } } };
                const c: A.B.C = a.b.c;
                return if (a.b.k == .y) c.v else 1;
            }
            """);
        cs.ShouldContain("public A__K k;");
        cs.ShouldContain("struct A__B__C");
        cs.ShouldContain("A__B__C c =");
    }

    [Fact]
    public void Two_parents_nesting_the_same_name_do_not_collide()
    {
        var cs = EmitZig("""
            const L = struct { k: Kind, pub const Kind = enum { a, b }; };
            const R = struct { k: Kind, pub const Kind = enum { x, y, z }; };
            pub fn main() u8 {
                const l: L = .{ .k = .b };
                const r: R = .{ .k = .z };
                return if (l.k == .b and r.k == .z) 42 else 1;
            }
            """);
        cs.ShouldContain("enum L__Kind");
        cs.ShouldContain("enum R__Kind");
    }

    [Fact]
    public void A_nested_name_does_not_leak_outside_its_parent()
    {
        var ex = Should.Throw<Exception>(() => EmitZig("""
            const N = struct { m: Mode, pub const Mode = enum { a, b }; };
            pub fn main() u8 { const m: Mode = .a; _ = m; return 0; }
            """));
        ex.Message.ShouldContain("Mode");
    }
}
