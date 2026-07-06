#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for GENERIC FUNCTIONS via <c>comptime</c> value parameters (wall-plan W3a) — call-site
/// monomorphization. A <c>comptime</c>-value parameter turns a function into a template: it is NOT
/// lowered once; a call instantiates a SPECIALIZED body per resolved comptime-argument tuple (over the
/// retained AST), emitted under a deterministic mangled name (<c>addN__10</c>) and memoized so a repeat
/// call reuses it. The comptime value is baked into the body as a literal, and a comptime-known <c>if</c>
/// inside an instance folds to its taken branch (so a recursive generic like <c>fib</c> prunes its base
/// case and terminates instead of instantiating forever). The instance bodies lower from a re-entrancy-safe
/// worklist drained after pass 2. V1 cuts (loud): a <c>comptime T: type</c> TYPE param (W3b), a generic
/// METHOD, and a non-constant comptime argument. End-to-end in the <c>comptime-param</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigComptimeParamTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigctp-{Guid.NewGuid():N}.zig");
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
    public void Distinct_comptime_values_get_distinct_mangled_instances()
    {
        // addN(10, ·) and addN(100, ·) instantiate two separate specialized functions.
        var cs = EmitZig("""
            fn addN(comptime N: i32, x: i32) i32 { return x + N; }
            pub fn main() u8 { return @intCast(addN(10, 5) - addN(100, -73)); }
            """);
        cs.ShouldContain("addN__10");
        cs.ShouldContain("addN__100");
    }

    [Fact]
    public void Comptime_value_is_baked_into_the_body_as_a_literal()
    {
        // The comptime N appears as a literal in each instance body — 10 in addN__10, 100 in addN__100.
        var cs = EmitZig("""
            fn addN(comptime N: i32, x: i32) i32 { return x + N; }
            pub fn main() u8 { return @intCast(addN(10, 5) + addN(100, 5)); }
            """);
        cs.ShouldContain("x + 10");
        cs.ShouldContain("x + 100");
        // Only the runtime parameter survives in the specialized signature (the comptime N is gone).
        cs.ShouldContain("addN__10(int x)");
    }

    [Fact]
    public void Repeated_call_with_the_same_value_reuses_one_memoized_instance()
    {
        // addN(10, a) and addN(10, b) are TWO calls but ONE instance — the definition appears once.
        var cs = EmitZig("""
            fn addN(comptime N: i32, x: i32) i32 { return x + N; }
            pub fn main() u8 { const a: i32 = 5; const b: i32 = 7; return @intCast(addN(10, a) + addN(10, b)); }
            """);
        Count(cs, "int addN__10(").ShouldBe(1);   // one DEFINITION despite two call sites
    }

    [Fact]
    public void Recursive_generic_prunes_its_base_case_and_terminates()
    {
        // `if (n < 2) return n;` is a comptime-if inside each instance: for fib__0/fib__1 it folds true,
        // and the sibling `return fib(n-1)+fib(n-2)` is dropped as comptime-dead — so instantiation
        // terminates at the base cases rather than recursing forever.
        var cs = EmitZig("""
            fn fib(comptime n: u32) u64 { if (n < 2) return n; return fib(n - 1) + fib(n - 2); }
            pub fn main() u8 { return @intCast(fib(6)); }
            """);
        cs.ShouldContain("fib__6");
        cs.ShouldContain("fib__1");
        cs.ShouldContain("fib__0");
        // The base cases return the folded literal, NOT a recursive call.
        cs.ShouldContain("return 0");
        cs.ShouldContain("return 1");
    }

    [Fact]
    public void Comptime_bound_loop_bakes_the_bound_per_instance()
    {
        // powi(3, ·) bakes the loop trip count as a literal in the specialized body.
        var cs = EmitZig("""
            fn powi(comptime n: u32, x: i64) i64 {
                var r: i64 = 1;
                var i: u32 = 0;
                while (i < n) : (i = i + 1) { r = r * x; }
                return r;
            }
            pub fn main() u8 { return @intCast(powi(3, 2)); }
            """);
        cs.ShouldContain("powi__3");
        cs.ShouldContain("i < 3u");   // the comptime bound baked into the loop condition
    }

    [Fact]
    public void Non_constant_comptime_argument_is_rejected_loudly()
    {
        // A comptime argument must be compile-time-known — a runtime value can't key an instantiation.
        var ex = Should.Throw<Exception>(() => EmitZig("""
            fn addN(comptime N: i32, x: i32) i32 { return x + N; }
            pub fn main() u8 { var k: i32 = 3; return @intCast(addN(k, 5)); }
            """));
        ex.Message.ShouldContain("compile-time-known");
    }

    [Fact]
    public void Comptime_type_parameter_is_rejected_as_W3b()
    {
        // A `comptime T: type` TYPE parameter is the next brick (W3b) — a clear, specific cut.
        var ex = Should.Throw<Exception>(() => EmitZig("""
            fn id(comptime T: type, x: T) T { return x; }
            pub fn main() u8 { return id(u8, 42); }
            """));
        ex.Message.ShouldContain("W3b");
    }

    [Fact]
    public void Comptime_param_method_is_rejected()
    {
        // W3a is free functions only — a generic METHOD needs machinery only the top-level passes run.
        var ex = Should.Throw<Exception>(() => EmitZig("""
            const S = struct {
                v: i32,
                fn addN(self: S, comptime N: i32) i32 { return self.v + N; }
            };
            pub fn main() u8 { const s: S = .{ .v = 5 }; return @intCast(s.addN(10)); }
            """));
        ex.Message.ShouldContain("method");
    }

    [Fact]
    public void Uninstantiated_generic_emits_no_body()
    {
        // A generic that is never called instantiates nothing — no specialized (or template) body is
        // emitted, exactly as real zig emits no code for an uninstantiated generic.
        var cs = EmitZig("""
            fn unused(comptime N: i32, x: i32) i32 { return x + N; }
            pub fn main() u8 { return 42; }
            """);
        cs.ShouldNotContain("unused__");   // no specialized instance
        cs.ShouldNotContain("int unused(");  // no template body either
    }
}
