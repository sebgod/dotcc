#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for the W4 lift (road-to-zig-std G4 blocker 2): a <c>type</c>-returning function's body is
/// now EVALUATED at comptime rather than required to be <c>[const X = T;]* return struct {…};</c>. A body
/// may return a type it did not declare — a delegating type-returning CALL (<c>std.ArrayList</c>'s
/// <c>return array_list.Aligned(T, null);</c>), a folded <c>switch (@typeInfo(T))</c>
/// (<c>std.meta.Child</c>), a folded <c>if</c>, <c>@Int(…)</c>, a bare <c>T</c> — and may open with a
/// comptime <c>if</c> whose taken arm returns early (<c>array_list.Aligned</c>'s own shape). A
/// delegating instance reifies nothing: it resolves to the type it returned, memoized under its own
/// mangled name. End-to-end in the <c>type_body_delegation</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigTypeBodyTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigtb-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private static string EmitZigMulti(string mainSource, params (string name, string source)[] siblings)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zigtb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var mainPath = Path.Combine(dir, "main.zig");
        File.WriteAllText(mainPath, mainSource);
        foreach (var (name, source) in siblings) { File.WriteAllText(Path.Combine(dir, name), source); }
        try { return Compiler.EmitCSharp(new[] { mainPath }); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_bare_type_return_resolves_to_that_type()
    {
        // `return T;` — once W4's "non-struct return" cut. The function IS the identity on types.
        var cs = EmitZig("""
            fn Id(comptime T: type) type { return T; }
            pub fn main() u8 { const x: Id(u8) = 42; return x; }
            """);
        cs.ShouldContain("byte x = 42");
        cs.ShouldNotContain("Id__u8");   // nothing reified — the result is `u8` itself
    }

    [Fact]
    public void A_delegating_call_resolves_to_the_delegate_instance()
    {
        // The `std.ArrayList(T) = array_list.Aligned(T, null)` shape, same module: the delegating
        // function reifies nothing; its result IS the delegate's instance, so both spellings name one type.
        var cs = EmitZig("""
            fn Aligned(comptime T: type, comptime alignment: ?u8) type {
                _ = alignment;
                return struct { v: T };
            }
            fn List(comptime T: type) type { return Aligned(T, null); }
            pub fn main() u8 {
                const a: List(u8) = .{ .v = 40 };
                const b: Aligned(u8, null) = a;
                return b.v + 2;
            }
            """);
        cs.ShouldContain("struct Aligned__u8_optnull");
        cs.ShouldNotContain("List__u8");
        cs.ShouldContain("Aligned__u8_optnull a =");
    }

    [Fact]
    public void A_delegating_chain_resolves_through_every_link()
    {
        var cs = EmitZig("""
            fn Box(comptime T: type) type { return struct { v: T }; }
            fn Mid(comptime T: type) type { return Box(T); }
            fn Top(comptime T: type) type { return Mid(T); }
            pub fn main() u8 { const t: Top(u8) = .{ .v = 42 }; return t.v; }
            """);
        cs.ShouldContain("struct Box__u8");
        cs.ShouldNotContain("Mid__u8");
        cs.ShouldNotContain("Top__u8");
    }

    [Fact]
    public void A_type_switch_over_typeInfo_folds_to_the_taken_prong()
    {
        // `std.meta.Child` verbatim in shape: the prong capture binds the payload, `info.child` is the type.
        var cs = EmitZig("""
            fn Child(comptime T: type) type {
                return switch (@typeInfo(T)) {
                    .array => |info| info.child,
                    .pointer => |info| info.child,
                    .optional => |info| info.child,
                    else => @compileError("Expected pointer, optional or array type"),
                };
            }
            pub fn main() u8 {
                const a: Child(*u8) = 40;
                const b: Child(?u16) = 2;
                return a + @as(u8, @intCast(b));
            }
            """);
        cs.ShouldContain("byte a = 40");
        cs.ShouldContain("ushort b = 2");
    }

    [Fact]
    public void A_type_switch_reaching_compileError_raises_it()
    {
        var ex = Should.Throw<Exception>(() => EmitZig("""
            fn Child(comptime T: type) type {
                return switch (@typeInfo(T)) {
                    .pointer => |info| info.child,
                    else => @compileError("Expected pointer type"),
                };
            }
            pub fn main() u8 { const a: Child(u8) = 40; return a; }
            """));
        ex.Message.ShouldContain("Expected pointer type");
    }

    [Fact]
    public void A_value_conditioned_if_selects_the_type()
    {
        // `return if (n > 8) u16 else u8;` over a comptime VALUE param — the condition is an ordinary
        // expression, folded because `n` is a comptime seed.
        var cs = EmitZig("""
            fn Fit(comptime n: u16) type { return if (n > 255) u16 else u8; }
            pub fn main() u8 {
                const small: Fit(200) = 40;
                const big: Fit(300) = 2;
                return small + @as(u8, @intCast(big));
            }
            """);
        cs.ShouldContain("byte small = 40");
        cs.ShouldContain("ushort big = 2");
    }

    [Fact]
    public void A_leading_comptime_if_returns_early_only_when_taken()
    {
        // `array_list.Aligned`'s opening: `if (alignment) |a| { if (…) return Aligned(T, null); }` — a
        // comptime-null alignment folds the whole `if` away and the walk continues to `return struct`;
        // a known one takes the early return and delegates.
        var cs = EmitZig("""
            fn Aligned(comptime T: type, comptime alignment: ?u8) type {
                if (alignment) |a| {
                    if (a == 1) return Aligned(T, null);
                }
                return struct { v: T };
            }
            pub fn main() u8 {
                const x: Aligned(u8, 1) = .{ .v = 40 };
                const y: Aligned(u8, null) = x;
                return y.v + 2;
            }
            """);
        cs.ShouldContain("struct Aligned__u8_optnull");
        cs.ShouldNotContain("struct Aligned__u8_opt1");   // the early return delegated instead of reifying
    }

    [Fact]
    public void A_type_equality_condition_folds()
    {
        // `T == u8` — both operands are types, so the comparison is comptime (and has no runtime meaning).
        var cs = EmitZig("""
            fn Wider(comptime T: type) type { return if (T == u8) u16 else T; }
            pub fn main() u8 {
                const a: Wider(u8) = 40;
                const b: Wider(u32) = 2;
                return @intCast(a + b);
            }
            """);
        cs.ShouldContain("ushort a = 40");
        cs.ShouldContain("uint b = 2");
    }

    [Fact]
    public void Type_equality_distinguishes_widths_dotcc_widens_alike()
    {
        // u21 and u32 both lower to a C# `uint`; in zig they are different types, and `T == u32` says so.
        var cs = EmitZig("""
            fn Pick(comptime T: type) type { return if (T == u32) u8 else u16; }
            pub fn main() u8 {
                const a: Pick(u32) = 40;
                const b: Pick(u21) = 2;
                return a + @as(u8, @intCast(b));
            }
            """);
        cs.ShouldContain("byte a = 40");
        cs.ShouldContain("ushort b = 2");
    }

    [Fact]
    public void A_delegated_int_type_keeps_its_declared_width()
    {
        // `return @Int(.unsigned, n);` — the width the delegated type was BUILT with rides the call site,
        // so `@bitSizeOf(U(21))` is 21, not the 32 dotcc widens a `u21` to.
        var cs = EmitZig("""
            fn U(comptime n: u16) type { return @Int(.unsigned, n); }
            pub fn main() u8 { return @bitSizeOf(U(21)) * 2; }
            """);
        cs.ShouldContain("return (byte)(21 * 2)");
        cs.ShouldNotContain("32 * 2");
    }

    [Fact]
    public void A_module_qualified_delegation_reifies_in_the_template_module()
    {
        // std.zig's own `pub fn ArrayList(comptime T: type) type { return array_list.Aligned(T, null); }`,
        // with a sibling module standing in for array_list.zig: the delegate is module-qualified.
        var cs = EmitZigMulti(
            "const shim = @import(\"./shim.zig\");\n" +
            "pub fn main() u8 {\n" +
            "    const box: shim.List(u8) = .{ .first = 40 };\n" +
            "    return box.first + 2;\n" +
            "}\n",
            ("shim.zig",
                "pub const list = @import(\"./list.zig\");\n" +
                "pub fn List(comptime T: type) type { return list.Aligned(T, null); }\n"),
            ("list.zig",
                "pub fn Aligned(comptime T: type, comptime alignment: ?u8) type {\n" +
                "    if (alignment) |a| { if (a == 1) return Aligned(T, null); }\n" +
                "    return struct { first: T };\n" +
                "}\n"));
        cs.ShouldContain("struct list__Aligned__u8_optnull");   // reified in (and named for) list.zig
        cs.ShouldNotContain("List__u8");
    }

    [Fact]
    public void A_self_delegating_body_is_a_dependency_loop()
    {
        var ex = Should.Throw<Exception>(() => EmitZig("""
            fn Loop(comptime T: type) type { return Loop(T); }
            pub fn main() u8 { const x: Loop(u8) = 1; return x; }
            """));
        ex.Message.ShouldContain("depends on itself");
    }

    [Fact]
    public void A_runtime_statement_in_a_type_body_is_rejected()
    {
        // A type body is comptime code; a statement with no comptime meaning is a loud cut, not skipped.
        var ex = Should.Throw<Exception>(() => EmitZig("""
            fn Bad(comptime T: type) type { var n: u8 = 0; n += 1; return T; }
            pub fn main() u8 { const x: Bad(u8) = 1; return x; }
            """));
        ex.Message.ShouldContain("a body statement must be");
    }

    [Fact]
    public void A_switch_statement_with_returning_prongs_selects_one()
    {
        // `std.meta.Elem`'s shape: a STATEMENT switch whose prongs `return`, a nested switch on the
        // pointer payload's `size`, fall-through `{}` prongs, and a trailing `@compileError` that only
        // fires if nothing returned. A slice's size class is `.slice` — the one dotcc keeps.
        var cs = EmitZig("""
            fn Elem(comptime T: type) type {
                switch (@typeInfo(T)) {
                    .array => |info| return info.child,
                    .pointer => |info| switch (info.size) {
                        .slice => return info.child,
                        else => {},
                    },
                    .optional => |info| return Elem(info.child),
                    else => {},
                }
                @compileError("Expected slice, array or optional type");
            }
            pub fn main() u8 {
                const a: Elem([4]u8) = 40;
                const b: Elem([]const u16) = 1;
                const c: Elem(?[2]u8) = 1;
                return a + @as(u8, @intCast(b)) + c;
            }
            """);
        cs.ShouldContain("byte a = 40");
        cs.ShouldContain("ushort b = 1");
        cs.ShouldContain("byte c = 1");
    }

    [Fact]
    public void A_switch_statement_falling_through_reaches_compileError()
    {
        var ex = Should.Throw<Exception>(() => EmitZig("""
            fn Elem(comptime T: type) type {
                switch (@typeInfo(T)) {
                    .array => |info| return info.child,
                    else => {},
                }
                @compileError("Expected an array type");
            }
            pub fn main() u8 { const a: Elem(u8) = 40; return a; }
            """));
        ex.Message.ShouldContain("Expected an array type");
    }

    [Fact]
    public void Comptime_value_consts_size_the_returned_type()
    {
        // `std.math.Log2Int` verbatim: a `comptime_int` guard, a typed comptime value const read off
        // `@typeInfo`, a `@clz` over it, and the width that sizes an `@Int`. `bits - 1` is a `u16` in zig
        // (no promotion), so `@clz` counts in 16 bits: clz16(63) = 10, 16 - 10 = 6 → `u6`.
        var cs = EmitZig("""
            fn Log2Int(comptime T: type) type {
                if (T == comptime_int) return comptime_int;
                const bits: u16 = @typeInfo(T).int.bits;
                const log2_bits = 16 - @clz(bits - 1);
                return @Int(.unsigned, log2_bits);
            }
            pub fn main() u8 { return @bitSizeOf(Log2Int(u64)) * 7; }
            """);
        cs.ShouldContain("return (byte)(6 * 7)");
    }

    [Fact]
    public void A_non_comptime_value_const_is_rejected()
    {
        var ex = Should.Throw<Exception>(() => EmitZig("""
            var g: u16 = 3;
            fn Bad(comptime T: type) type { const n = g; _ = n; return T; }
            pub fn main() u8 { const x: Bad(u8) = 1; return x; }
            """));
        ex.Message.ShouldContain("must be compile-time-known");
    }

    [Fact]
    public void A_runtime_bit_count_counts_in_the_operands_zig_width()
    {
        // No integer promotion in zig: `b - 1` on a `u16` is a `u16`, so the runtime `@clz` must see a
        // 16-bit operand, not the `int` C#'s arithmetic would hand it.
        var cs = EmitZig("""
            pub fn main() u8 {
                var b: u16 = 64;
                b += 0;
                return @intCast(@clz(b - 1));
            }
            """);
        cs.ShouldContain("ZigMath.Clz((ushort)(");
    }
}
