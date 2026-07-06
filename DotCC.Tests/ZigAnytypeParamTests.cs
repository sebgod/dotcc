#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for GENERIC FUNCTIONS via <c>anytype</c> parameters (wall-plan W5) — the cheap capstone on
/// the monomorphization spine. An <c>a: anytype</c> parameter is a HYBRID of a comptime TYPE key and a
/// runtime slot: unlike a <c>comptime T: type</c> parameter (an explicit type argument consumed at
/// compile time), an <c>anytype</c> parameter's type is INFERRED from the actual argument
/// (<c>T := @TypeOf(arg)</c>) AND the argument is still passed at runtime. The inferred type keys /
/// mangles the instance and seeds the signature (so a <c>@TypeOf(param)</c> return type resolves), while
/// the instance body binds the parameter as an ordinary runtime symbol of the inferred type — so
/// duck-typed use (member access, arithmetic) lowers against the concrete type, a mismatch failing PER
/// INSTANTIATION (real Zig / C++-template behavior). Reuses the W3 worklist + memoization. V1 cuts
/// (loud): a generic METHOD, and an <c>anytype</c> parameter on an <c>extern</c> prototype. End-to-end in
/// the <c>anytype-param</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigAnytypeParamTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigat-{Guid.NewGuid():N}.zig");
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
    public void Distinct_inferred_types_get_distinct_instances()
    {
        // add(3, 4) infers (i32, i32) and add(1.5, 2.5) infers (f64, f64) — two specializations mangled
        // by the INFERRED argument types, each a runtime slot (unlike a comptime TYPE arg). The
        // `@TypeOf(a)` return type resolves to the first param's inferred type.
        var cs = EmitZig("""
            fn add(a: anytype, b: anytype) @TypeOf(a) { return a + b; }
            pub fn main() u8 {
                const i: i32 = add(3, 4);
                const f: f64 = add(1.5, 2.5);
                return @intCast(i + @as(i32, @intFromFloat(f)));
            }
            """);
        cs.ShouldContain("int add__i32_i32(int a, int b)");     // both params + return → int
        cs.ShouldContain("double add__f64_f64(double a, double b)");
    }

    [Fact]
    public void Same_inferred_type_keys_one_memoized_instance()
    {
        // Two calls whose arguments infer the SAME types reuse ONE memoized instance (keyed by the
        // resolved inferred types, like the comptime-TYPE-param memo).
        var cs = EmitZig("""
            fn add(a: anytype, b: anytype) @TypeOf(a) { return a + b; }
            pub fn main() u8 {
                const x: i32 = add(1, 2);
                const y: i32 = add(3, 4);
                return @intCast(x + y);
            }
            """);
        Count(cs, "int add__i32_i32(").ShouldBe(1);   // one DEFINITION despite two calls
    }

    [Fact]
    public void Anytype_return_type_is_the_inferred_argument_type()
    {
        // `fn identity(x: anytype) @TypeOf(x)` — the return type IS the inferred parameter type, so the
        // i32 and u8 instances differ in both the parameter and the return spelling.
        var cs = EmitZig("""
            fn identity(x: anytype) @TypeOf(x) { return x; }
            pub fn main() u8 { return @intCast(identity(@as(i32, 30)) + identity(@as(u8, 12))); }
            """);
        cs.ShouldContain("int identity__i32(int x)");
        cs.ShouldContain("byte identity__u8(byte x)");
    }

    [Fact]
    public void Duck_typed_member_access_lowers_against_the_concrete_type()
    {
        // The parameter is an ordinary runtime symbol of the inferred type in the body, so `p.x` (a
        // struct field) and `s.len` (a slice length) lower against the concrete type — no reflection.
        var cs = EmitZig("""
            const Point = struct { x: i32, y: i32 };
            fn getX(p: anytype) i32 { return p.x; }
            fn firstLen(s: anytype) usize { return s.len; }
            pub fn main() u8 {
                const p = Point{ .x = 42, .y = 7 };
                const arr = [_]i32{ 1, 2, 3 };
                return @intCast(getX(p) + @as(i32, @intCast(firstLen(arr[0..]))));
            }
            """);
        cs.ShouldContain("int getX__Point(Point p)");   // inferred type → the struct, mangled by name
        cs.ShouldContain("return p.x;");                 // field access against the concrete struct
        cs.ShouldContain("firstLen__");                  // a slice instance (mangled from the fat-pointer type)
        cs.ShouldContain("return s.Len;");               // `.len` against the concrete slice
    }

    [Fact]
    public void Anytype_method_is_rejected()
    {
        // W5, like W3, is free functions only — a generic method needs the top-level-pass machinery.
        var ex = Should.Throw<Exception>(() => EmitZig("""
            const S = struct {
                v: i32,
                fn get(self: S, x: anytype) i32 { return self.v + @as(i32, @intCast(x)); }
            };
            pub fn main() u8 { const s: S = .{ .v = 5 }; return @intCast(s.get(@as(i32, 3))); }
            """));
        ex.Message.ShouldContain("method");
    }

    [Fact]
    public void Anytype_extern_parameter_is_rejected()
    {
        // An `extern` prototype is a C-ABI signature — `anytype` (a monomorphization key, not an ABI
        // slot) makes no sense on one, and real zig rejects it too.
        var ex = Should.Throw<Exception>(() => EmitZig("""
            extern fn bad(x: anytype) c_int;
            pub fn main() u8 { return 0; }
            """));
        ex.Message.ShouldContain("anytype");
    }

    [Fact]
    public void Uninstantiated_anytype_generic_emits_no_body()
    {
        // An anytype generic that is never called instantiates nothing — no specialized body, and no
        // template body either (the template symbol carries only a placeholder signature).
        var cs = EmitZig("""
            fn unused(x: anytype) i32 { return @as(i32, @intCast(x)); }
            pub fn main() u8 { return 42; }
            """);
        cs.ShouldNotContain("unused__");    // no specialized instance
        cs.ShouldNotContain(" unused(");     // no template body either
    }
}
