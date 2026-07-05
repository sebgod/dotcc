#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for Zig type-as-value support (wall-plan W1 — the comptime-type foundation): a
/// <c>const T = &lt;type&gt;;</c> binds a NAME to a type (Zig's "types are values" core, so the RHS
/// already parses via <c>CurlySuffix → Type</c> — no grammar change), and the alias then resolves in
/// any type position. The composed type prefixes (<c>*T</c>, <c>?T</c>, <c>[]T</c>) ride the ordinary
/// <c>LowerType</c> over the aliased element for free; <c>@TypeOf(expr)</c> yields the operand's
/// synthesized type (unevaluated), usable both in a type position and as a <c>const</c> RHS; and a
/// runtime <c>var t: type</c> is rejected loudly (Zig's <c>type</c> is comptime-only). No runtime decl
/// is emitted for an alias. Differential end-to-end in the <c>type-value</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigTypeValueTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigtype-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private const string TrivialMain = "\npub fn main() u8 { return 0; }\n";

    [Fact]
    public void Bare_type_alias_resolves_in_a_signature()
    {
        // `const T = i32;` binds T to the type i32; the alias then resolves in a param AND return
        // position. (Before W1 a bare-type-name const errored — it fell through to a runtime global
        // whose `i32` RHS is not a value — so this compiling at all proves the alias, no runtime decl.)
        var cs = EmitZig("""
            const T = i32;
            pub fn zid(x: T) T { return x; }
            """ + TrivialMain);
        cs.ShouldContain("int zid(int x)");
    }

    [Fact]
    public void Pointer_prefix_composes_over_an_alias()
    {
        // `const P = *T;` — a type prefix over ANOTHER alias. Both resolve through _typeAliases, so
        // P lowers to `int*` and the deref `p.*` reads through it.
        var cs = EmitZig("""
            const T = i32;
            const P = *T;
            pub fn zderef(p: P) T { return p.*; }
            """ + TrivialMain);
        cs.ShouldContain("int zderef(int* p)");
    }

    [Fact]
    public void Optional_prefix_composes_over_an_alias()
    {
        // `?T` over an alias → C# `int?` (the value-optional lowering, unchanged).
        var cs = EmitZig("""
            const T = i32;
            pub fn zopt(x: T) ?T { return x; }
            """ + TrivialMain);
        cs.ShouldContain("int? zopt(int x)");
    }

    [Fact]
    public void Slice_prefix_composes_over_an_alias()
    {
        // `[]T` over an alias → the runtime `Slice<T>` fat pointer, and `.len` rides the slice member.
        var cs = EmitZig("""
            const T = u8;
            pub fn zslice(s: []T) usize { return s.len; }
            """ + TrivialMain);
        cs.ShouldContain("Slice<byte>");
        cs.ShouldContain("zslice");
    }

    [Fact]
    public void TypeOf_resolves_in_a_type_position()
    {
        // `var y: @TypeOf(x) = x;` — @TypeOf yields x's synthesized type (i32 → int), unevaluated.
        var cs = EmitZig("""
            pub fn main() u8 {
                var x: i32 = 5;
                var y: @TypeOf(x) = x;
                _ = y;
                return 0;
            }
            """);
        cs.ShouldContain("int y = x");
    }

    [Fact]
    public void Local_alias_via_typeof_binds_a_body_local()
    {
        // The monomorphization-shaped case: a local `const T = @TypeOf(a);` (T ← i64 → long) then
        // `var acc: T = a;`. The const emits NO runtime decl; the alias resolves the local's type.
        var cs = EmitZig("""
            pub fn zwiden(a: i64) i64 {
                const T = @TypeOf(a);
                var acc: T = a;
                acc = acc + 1;
                return acc;
            }
            """ + TrivialMain);
        cs.ShouldContain("long acc = a");
    }

    [Fact]
    public void Local_bare_type_alias_binds_a_body_local()
    {
        // A local `const T = i32;` inside a body — recorded with no decl (empty Seq), then used as
        // the type of a following local.
        var cs = EmitZig("""
            pub fn main() u8 {
                const T = i32;
                var x: T = 5;
                _ = x;
                return 0;
            }
            """);
        cs.ShouldContain("int x = 5");
    }

    [Fact]
    public void Curated_generic_std_type_can_be_aliased()
    {
        // `const List = std.ArrayList(i32);` — a curated generic std type (wall-plan W0) bound to a
        // name; the alias resolves to the runtime ZigList<int> in a return position, `.empty` → default.
        var cs = EmitZig("""
            const std = @import("std");
            const List = std.ArrayList(i32);
            pub fn zmake() List { return .empty; }
            """ + TrivialMain);
        cs.ShouldContain("ZigList<int> zmake(");
    }

    [Fact]
    public void Runtime_var_of_type_type_is_rejected_loudly()
    {
        // Zig's `type` is comptime-only: a runtime `var t: type` (or a `fn f(t: type)` param, which is
        // W3's comptime param) is illegal in real zig too — reject rather than mis-lower.
        var ex = Should.Throw<Exception>(() => EmitZig("""
            pub fn main() u8 {
                var t: type = i32;
                _ = t;
                return 0;
            }
            """));
        ex.Message.ShouldContain("comptime-only");
    }
}
