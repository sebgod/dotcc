#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for TYPE-RETURNING functions (wall-plan W4) — <c>fn Pair(comptime T: type) type { return
/// struct { a: T, b: T }; }</c>. A type-returning function is a COMPTIME type constructor: it emits no
/// runtime code; each use in a TYPE position REIFIES a fresh struct per resolved type argument
/// (<c>Pair__i32</c>, <c>Pair__f64</c>), memoized so the same argument reuses one struct. Fields typed
/// <c>T</c> get the concrete type; <c>?*@This()</c> becomes a self-pointer. Keyed by the RESOLVED type,
/// so an alias for <c>i32</c> shares the <c>__i32</c> reification. V1 cuts (loud): a method / <c>const</c>
/// member in the returned struct, a non-struct return, a runtime/value parameter on the type function.
/// End-to-end in the <c>type-returning-fn</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigTypeReturningFnTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigtrf-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    /// <summary>Count non-overlapping occurrences of <paramref name="needle"/> in <paramref name="s"/>.</summary>
    private static int Count(string s, string needle)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [Fact]
    public void Distinct_type_arguments_reify_distinct_structs()
    {
        // Pair(i32) and Pair(f64) reify two separate structs whose FIELD types are the resolved type.
        var cs = EmitZig("""
            fn Pair(comptime T: type) type { return struct { a: T, b: T }; }
            pub fn main() u8 {
                const pi: Pair(i32) = .{ .a = 3, .b = 4 };
                const pf: Pair(f64) = .{ .a = 1.5, .b = 2.5 };
                return @intCast(pi.a + pi.b + @as(i32, @intFromFloat(pf.a)));
            }
            """);
        cs.ShouldContain("struct Pair__i32");
        cs.ShouldContain("struct Pair__f64");
        cs.ShouldContain("public int a;");      // T → int in Pair__i32
        cs.ShouldContain("public double a;");   // T → double in Pair__f64
    }

    [Fact]
    public void Type_argument_alias_keys_the_same_reified_struct()
    {
        // `const PairI32 = Pair(i32);` — a top-level alias (resolved after the fn is declared). Using
        // both `PairI32` and `Pair(i32)` reifies ONE struct (keyed by the resolved type, not the spelling).
        var cs = EmitZig("""
            fn Pair(comptime T: type) type { return struct { a: T, b: T }; }
            const PairI32 = Pair(i32);
            pub fn main() u8 {
                const x: PairI32 = .{ .a = 1, .b = 2 };
                const y: Pair(i32) = .{ .a = 3, .b = 4 };
                return @intCast(x.a + x.b + y.a + y.b);
            }
            """);
        Count(cs, "struct Pair__i32").ShouldBe(1);   // one reified struct despite two spellings
    }

    [Fact]
    public void Self_referential_field_via_This_reifies_a_self_pointer()
    {
        // `next: ?*const @This()` in the returned struct resolves @This() to the in-progress reified
        // type, so Node(i32) gets a `Node__i32*` self-pointer field.
        var cs = EmitZig("""
            fn Node(comptime T: type) type { return struct { value: T, next: ?*const @This() }; }
            pub fn main() u8 {
                const tail: Node(i32) = .{ .value = 20, .next = null };
                const head: Node(i32) = .{ .value = 10, .next = &tail };
                return @intCast(head.value + head.next.?.value);
            }
            """);
        cs.ShouldContain("struct Node__i32");
        cs.ShouldContain("public int value;");
        cs.ShouldContain("Node__i32* next;");   // @This() → the reified type, a self-pointer
    }

    [Fact]
    public void Method_in_the_returned_struct_is_rejected()
    {
        // V1 reifies fields-only (like the W2 in-fn container) — a method in the returned type is a cut.
        var ex = Should.Throw<Exception>(() => EmitZig("""
            fn Box(comptime T: type) type {
                return struct { x: T, fn get(self: @This()) T { return self.x; } };
            }
            pub fn main() u8 { const b: Box(u8) = .{ .x = 42 }; return b.x; }
            """));
        ex.Message.ShouldContain("fields-only");
    }

    [Fact]
    public void Non_struct_return_is_rejected()
    {
        // V1's body must be `return struct {…};` — returning a bare type (`return T;`) is a cut.
        var ex = Should.Throw<Exception>(() => EmitZig("""
            fn Id(comptime T: type) type { return T; }
            pub fn main() u8 { const x: Id(u8) = 42; return x; }
            """));
        ex.Message.ShouldContain("non-struct");
    }

    [Fact]
    public void Runtime_parameter_on_a_type_function_is_rejected()
    {
        // A type-returning function's parameters must all be `comptime T: type` in V1 — a runtime
        // parameter is a loud cut (checked at declaration, independent of any use).
        var ex = Should.Throw<Exception>(() => EmitZig("""
            fn Bad(x: i32) type { return struct { a: i32 }; }
            pub fn main() u8 { return 0; }
            """));
        ex.Message.ShouldContain("comptime");
    }

    [Fact]
    public void Type_returning_generic_folds_a_captured_if_to_a_type_in_a_multi_statement_body()
    {
        // road-to-zig-std S4b pt2 + S4c — a type-returning generic with a comptime `?T` param whose
        // MULTI-statement body computes a type via a captured-`if` fold, then returns a struct using it.
        // The `Aligned(T, alignment)` `const Slice = if (alignment) |a| … else []T;` shape.
        var cs = EmitZig("""
            fn Store(comptime T: type, comptime cap: ?u8) type {
                const Slice = if (cap) |n| [n]T else []T;
                return struct { data: Slice, len: usize };
            }
            pub fn main() u8 {
                var a: Store(u8, 3) = undefined;
                a.data[0] = 42; a.len = 1;
                return a.data[0];
            }
            """);
        cs.ShouldContain("Store__u8_opt3");   // instance keyed by the resolved type + comptime optional payload
        // The `const Slice = if (cap) |n| [n]T else []T` folded to `[3]u8` (a fixed-buffer field), not a
        // slice — the payload branch was taken with n bound to 3.
        cs.ShouldContain("fixed byte data[3]");    // the then (fixed-array) branch
        cs.ShouldNotContain("Slice<byte> data");   // NOT the else (slice) branch for the payload instance
    }

    [Fact]
    public void Type_returning_generic_folds_a_null_optional_to_the_else_type()
    {
        // The `null` argument selects the else-branch type (`[]T` → a Slice field) — the std.ArrayList
        // `Aligned(T, null)` path.
        var cs = EmitZig("""
            fn Store(comptime T: type, comptime cap: ?u8) type {
                const Slice = if (cap) |n| [n]T else []T;
                return struct { data: Slice, len: usize };
            }
            pub fn main() u8 {
                var buf = [_]u8{ 42, 0 };
                const s: Store(u8, null) = .{ .data = &buf, .len = 2 };
                return s.data[0];
            }
            """);
        cs.ShouldContain("Store__u8_optnull");
        cs.ShouldContain("Slice<byte> data");   // the else (slice) branch was folded for `null`
    }
}
