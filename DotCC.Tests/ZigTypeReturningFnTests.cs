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
/// so an alias for <c>i32</c> shares the <c>__i32</c> reification.
/// <para>road-to-zig-std G4 lifted the fields-only V1 cut: the reified struct also carries <c>const</c>
/// members (including <c>const Self = @This();</c>) and METHODS — each method declared under the mangled
/// container with its body deferred to a top-level drain, so it lowers to the same
/// <c>Container_method</c> free function an ordinary container's method does. Remaining loud cuts: a
/// NESTED container member, a non-struct return, a runtime parameter on the type function, and (inherited
/// from W3/W4/W5) a generic or <c>type</c>-returning METHOD.</para>
/// End-to-end in the <c>type-returning-fn</c> and <c>generic-container-methods</c> zig-oracle programs.
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
    public void Method_in_the_returned_struct_is_reified_as_a_mangled_free_function()
    {
        // road-to-zig-std G4 (lifts W4's fields-only V1 cut): a method in the returned struct is declared
        // under the MANGLED container, exactly like an ordinary container's method — so it lowers to the
        // `Container_method` free function and a call site binds to it.
        var cs = EmitZig("""
            fn Box(comptime T: type) type {
                return struct { x: T, fn get(self: @This()) T { return self.x; } };
            }
            pub fn main() u8 { const b: Box(u8) = .{ .x = 42 }; return b.get(); }
            """);
        cs.ShouldContain("struct Box__u8");
        cs.ShouldContain("Box__u8_get(Box__u8 self)");   // method → mangled free fn on the reified type
        cs.ShouldContain("Box__u8_get(b)");              // and the call site binds to it
    }

    [Fact]
    public void Reified_methods_resolve_Self_alias_and_sibling_calls()
    {
        // `const Self = @This();` registers against the MANGLED container, so a `*Self` receiver lowers to
        // a pointer to the reified struct; and a sibling method call inside a method body resolves through
        // the same `_methods` table (the body is drained at top level with the container in scope).
        var cs = EmitZig("""
            fn Box(comptime T: type) type {
                return struct {
                    v: T,
                    const Self = @This();
                    pub fn get(self: *const Self) T { return self.v; }
                    pub fn twice(self: *const Self) T { return self.get() + self.get(); }
                };
            }
            pub fn main() u8 { const b: Box(u8) = .{ .v = 21 }; return b.twice(); }
            """);
        cs.ShouldContain("Box__u8_get(Box__u8* self)");    // `*Self` → the reified type's pointer
        cs.ShouldContain("Box__u8_twice(Box__u8* self)");
        cs.ShouldContain("Box__u8_get(self)");              // sibling call inside the method body
    }

    [Fact]
    public void Reified_type_reached_through_an_alias_exposes_its_static_methods()
    {
        // A reified struct has no source-level name, so its alias is the ONLY way to reach a static
        // (receiverless) method — the `ArrayList.init`/`.empty` constructor idiom. `C.init()` must bind to
        // the reified type's mangled method, not fail as an unresolved identifier.
        var cs = EmitZig("""
            fn Counter(comptime T: type, comptime start: u8) type {
                return struct {
                    n: T,
                    const Self = @This();
                    pub fn init() Self { return .{ .n = start }; }
                    pub fn get(self: *const Self) T { return self.n; }
                };
            }
            const C = Counter(u8, 40);
            pub fn main() u8 { const c = C.init(); return c.get(); }
            """);
        cs.ShouldContain("Counter__u8_40_init()");
        cs.ShouldContain("n = 40");        // the comptime value seed, NOT `40u` (would not assign to byte)
        cs.ShouldNotContain("n = 40u");
    }

    [Fact]
    public void Reified_type_is_reachable_directly_as_a_call_base()
    {
        // `Box(u8).make(…)` — the idiomatic spelling (`std.ArrayList(u8).init(…)`) names the reified
        // container directly, with no intervening alias. An ordinary call base (`getBox().get()`) must
        // still lower as an instance method on the returned value, not be mistaken for a static call.
        var cs = EmitZig("""
            fn Box(comptime T: type) type {
                return struct {
                    v: T,
                    const Self = @This();
                    pub fn make(x: T) Self { var s: Self = undefined; s.v = x; return s; }
                    pub fn get(self: *const Self) T { return self.v; }
                };
            }
            fn getBox() Box(u8) { return Box(u8).make(7); }
            pub fn main() u8 { return Box(u8).make(35).v + getBox().get(); }
            """);
        cs.ShouldContain("Box__u8_make(35)");   // static call straight off the generic call base
        cs.ShouldContain("Box__u8_get(");       // and the ordinary call base still resolves as instance
    }

    [Fact]
    public void Value_const_in_the_returned_struct_is_reified()
    {
        // A `const` member of the returned struct registers against the mangled container, so the
        // qualified `Self.NAME` form inlines its comptime RHS inside a method.
        var cs = EmitZig("""
            fn Buf(comptime T: type) type {
                return struct {
                    v: T,
                    const Self = @This();
                    const CAP = 4;
                    pub fn cap(self: *const Self) usize { _ = self; return Self.CAP; }
                };
            }
            pub fn main() u8 { const b: Buf(u8) = .{ .v = 1 }; return @intCast(b.cap()); }
            """);
        cs.ShouldContain("Buf__u8_cap(Buf__u8* self)");
        cs.ShouldContain("return 4");   // Self.CAP inlined at the use site
    }

    [Fact]
    public void Nested_container_in_the_returned_struct_is_rejected()
    {
        // The remaining member cut: a nested container decl in the reified struct would need the nested
        // type bound under a parent-mangled name scoped to a REIFIED parent — deferred, so it's loud.
        var ex = Should.Throw<Exception>(() => EmitZig("""
            fn Outer(comptime T: type) type {
                return struct { v: T, const Inner = struct { z: u8 }; };
            }
            pub fn main() u8 { const o: Outer(u8) = .{ .v = 1 }; return o.v; }
            """));
        ex.Message.ShouldContain("nested container member");
    }

    [Fact]
    public void Generic_method_in_the_returned_struct_is_rejected()
    {
        // A method of the reified struct is an ordinary method, so it inherits the standing W3/W5 cut: a
        // `comptime`/`anytype` parameter on a METHOD is not supported (free functions only).
        var ex = Should.Throw<Exception>(() => EmitZig("""
            fn Box(comptime T: type) type {
                return struct {
                    v: T,
                    const Self = @This();
                    pub fn as(self: *const Self, comptime U: type) U { return @intCast(self.v); }
                };
            }
            pub fn main() u8 { const b: Box(u8) = .{ .v = 5 }; return b.as(u8); }
            """));
        ex.Message.ShouldContain("generic method");
    }

    [Fact]
    public void Type_returning_method_in_the_returned_struct_is_rejected()
    {
        // Likewise the standing W4 cut — a `type`-returning METHOD (`Aligned`'s nested `SentinelSlice`).
        var ex = Should.Throw<Exception>(() => EmitZig("""
            fn Box(comptime T: type) type {
                return struct { v: T, pub fn Elem() type { return T; } };
            }
            pub fn main() u8 { const b: Box(u8) = .{ .v = 5 }; return b.v; }
            """));
        ex.Message.ShouldContain("`type`-returning method");
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
