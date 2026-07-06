#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for GENERIC FUNCTIONS via <c>comptime T: type</c> TYPE parameters (wall-plan W3b) — the
/// second half of call-site monomorphization. Unlike a comptime VALUE parameter (W3a), a TYPE parameter
/// makes the later parameter / return types depend on <c>T</c>, so the signature CANNOT be lowered once
/// at template time: each call resolves the type argument to a concrete type, seeds <c>T ↦ that type</c>,
/// and lowers a SPECIALIZED signature + body — <c>maxOf(i32, …)</c> emits <c>int maxOf__i32(int, int)</c>
/// while <c>maxOf(f64, …)</c> emits <c>double maxOf__f64(double, double)</c>. The instance is mangled by
/// the RESOLVED type (an alias for <c>i32</c> keys the same <c>__i32</c> instance), memoized, and its body
/// drained from the re-entrancy-safe worklist. V1 cuts (loud): a <c>type</c>-RETURNING function (W4) and a
/// generic METHOD. End-to-end in the <c>comptime-type-param</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigComptimeTypeParamTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigcttp-{Guid.NewGuid():N}.zig");
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
    public void Distinct_type_arguments_get_distinct_per_instance_signatures()
    {
        // maxOf(i32, ·) and maxOf(f64, ·) instantiate two specializations whose SIGNATURES differ —
        // the runtime parameter AND return types are the concrete type argument, not a shared template.
        var cs = EmitZig("""
            fn maxOf(comptime T: type, a: T, b: T) T { return if (a > b) a else b; }
            pub fn main() u8 { return @intCast(maxOf(i32, 3, 7) - @as(i32, @intFromFloat(maxOf(f64, 2.5, 1.5)))); }
            """);
        cs.ShouldContain("int maxOf__i32(int a, int b)");
        cs.ShouldContain("double maxOf__f64(double a, double b)");
    }

    [Fact]
    public void Type_argument_spelled_as_an_alias_keys_the_same_instance()
    {
        // `const I = i32;` — maxOf(I, ·) and maxOf(i32, ·) resolve to the SAME type, so they share one
        // memoized `__i32` instance (keyed by the resolved type, not the source spelling).
        var cs = EmitZig("""
            const I = i32;
            fn maxOf(comptime T: type, a: T, b: T) T { return if (a > b) a else b; }
            pub fn main() u8 {
                const x: i32 = maxOf(I, 1, 2);
                const y: i32 = maxOf(i32, 3, 4);
                return @intCast(x + y);
            }
            """);
        Count(cs, "int maxOf__i32(").ShouldBe(1);   // one DEFINITION despite two spellings
        cs.ShouldNotContain("maxOf__I(");            // NOT keyed by the alias name
    }

    [Fact]
    public void Type_parameter_specializes_the_runtime_parameter_type()
    {
        // identity(u8, ·) → a `byte` parameter/return; identity(i32, ·) → an `int` one.
        var cs = EmitZig("""
            fn identity(comptime T: type, x: T) T { return x; }
            pub fn main() u8 { return @intCast(identity(i32, 30) + identity(u8, 12)); }
            """);
        cs.ShouldContain("int identity__i32(int x)");
        cs.ShouldContain("byte identity__u8(byte x)");
    }

    [Fact]
    public void Type_parameter_resolves_inside_the_body_via_sizeof()
    {
        // `@sizeOf(T)` inside the body folds against the seeded T — 4 for i32, 8 for f64 — so the two
        // instances return different baked literals (proof T resolves in the body, not just the signature).
        var cs = EmitZig("""
            fn sizeOfType(comptime T: type) usize { return @sizeOf(T); }
            pub fn main() u8 { return @intCast(sizeOfType(i32) + sizeOfType(f64)); }
            """);
        cs.ShouldContain("ulong sizeOfType__i32()");
        cs.ShouldContain("ulong sizeOfType__f64()");
        cs.ShouldContain("sizeof(int)");     // @sizeOf(T) resolved T → int  (C# folds it to 4)
        cs.ShouldContain("sizeof(double)");  // @sizeOf(T) resolved T → double  (folds to 8)
    }

    [Fact]
    public void Type_returning_function_is_rejected_as_W4()
    {
        // `fn F(comptime T: type) type { … }` returns a TYPE — the next brick (W4), a clear cut.
        var ex = Should.Throw<Exception>(() => EmitZig("""
            fn Box(comptime T: type) type { return T; }
            pub fn main() u8 { const B = Box(u8); const x: B = 42; return x; }
            """));
        ex.Message.ShouldContain("W4");
    }

    [Fact]
    public void Comptime_type_parameter_method_is_rejected()
    {
        // W3b, like W3a, is free functions only — a generic METHOD needs the top-level-pass machinery.
        var ex = Should.Throw<Exception>(() => EmitZig("""
            const S = struct {
                v: i32,
                fn asType(self: S, comptime T: type) T { return @intCast(self.v); }
            };
            pub fn main() u8 { const s: S = .{ .v = 5 }; return s.asType(u8); }
            """));
        ex.Message.ShouldContain("method");
    }

    [Fact]
    public void Uninstantiated_type_generic_emits_no_body()
    {
        // A type-param generic that is never called instantiates nothing — no specialized body, and no
        // template body either (the template symbol carries only a placeholder signature).
        var cs = EmitZig("""
            fn unused(comptime T: type, x: T) T { return x; }
            pub fn main() u8 { return 42; }
            """);
        cs.ShouldNotContain("unused__");    // no specialized instance
        cs.ShouldNotContain(" unused(");     // no template body either
    }
}
