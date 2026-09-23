#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for generic functions reached through the module graph (road-to-zig-std G3 — the wall
/// <c>std.fmt.bufPrint(&amp;buf, "{d}", .{42})</c> hit first: "expected 0 argument(s), got 3"). A call
/// <c>util.f(args)</c> to a GENERIC export used to bind the template's placeholder signature; it now
/// instantiates in the exporting module, with every argument read in the caller's scope. Also pins the
/// two pieces bufPrint's signature needed beside it: a comptime STRING parameter
/// (<c>comptime fmt: []const u8</c>) and a named container-level <c>const</c> folding as a comptime
/// argument — and the <c>.len</c>-of-a-string-literal miscompile the oracle program exposed.
/// End-to-end in the <c>import_generic_fn</c> and <c>string_literal_len</c> zig-oracle programs.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigCrossModuleGenericTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigxm-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private static string EmitZigMulti(string mainSource, params (string name, string source)[] siblings)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zigxm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var mainPath = Path.Combine(dir, "main.zig");
        File.WriteAllText(mainPath, mainSource);
        foreach (var (name, source) in siblings) { File.WriteAllText(Path.Combine(dir, name), source); }
        try { return Compiler.EmitCSharp(new[] { mainPath }); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private const string Util = """
        pub fn maxOf(comptime T: type, a: T, b: T) T { return if (a > b) a else b; }
        pub fn addN(comptime n: u8, x: u8) u8 { return x + n; }
        pub fn twice(x: anytype) @TypeOf(x) { return x + x; }
        pub fn lenOf(comptime s: []const u8) u8 { return @intCast(s.len); }
        """;

    [Fact]
    public void A_cross_module_generic_call_instantiates_in_the_exporting_module()
    {
        var cs = EmitZigMulti("""
            const util = @import("util.zig");
            pub fn main() u8 {
                const T = u8;
                return util.maxOf(T, 10, 7) + util.addN(20, 3) + util.twice(@as(u8, 3));
            }
            """, ("util.zig", Util));
        // One specialized instance per call, keyed by the caller's resolved arguments — the caller-side
        // alias `T` resolved in the CALLER to `u8`; the comptime value baked in; the anytype inferred.
        // Each instance emits under its module's prefix (`util__`), like any function the import declares.
        cs.ShouldContain("util__maxOf__u8(10, 7)");
        cs.ShouldContain("util__addN__20(3)");
        cs.ShouldContain("util__twice__u8((byte)3)");
        cs.ShouldContain("static unsafe byte util__maxOf__u8(byte a, byte b)");
        cs.ShouldNotContain("maxOf()");   // the placeholder template signature is never called
    }

    [Fact]
    public void A_comptime_string_parameter_keys_one_instance_per_distinct_string()
    {
        var cs = EmitZigMulti("""
            const util = @import("util.zig");
            const S = "abc";
            pub fn main() u8 {
                return util.lenOf(S) + util.lenOf("abc") + util.lenOf("hello");
            }
            """, ("util.zig", Util));
        // The same bytes — through a caller-side const or spelled inline — share ONE instance; a
        // different string is its own. The body substitutes the string, so `.len` folds (3, not 4).
        var call = System.Text.RegularExpressions.Regex.Match(
            cs, @"return \(byte\)\((\w+)\(\) \+ (\w+)\(\) \+ (\w+)\(\)\);");
        call.Success.ShouldBeTrue();
        call.Groups[1].Value.ShouldStartWith("util__lenOf__s");
        call.Groups[2].Value.ShouldBe(call.Groups[1].Value);
        call.Groups[3].Value.ShouldNotBe(call.Groups[1].Value);
        System.Text.RegularExpressions.Regex.Matches(cs, @"static unsafe byte util__lenOf__s[0-9a-f]{8}\(\)").Count.ShouldBe(2);
        cs.ShouldContain("return (byte)3UL;");
        cs.ShouldContain("return (byte)5UL;");
    }

    [Fact]
    public void An_imported_function_emits_under_its_module_prefix()
    {
        // Every module's functions land in ONE emitted class, so `util.f` and the root's own `f` were two
        // `static byte f()` methods: C# CS0111, from a dotcc run that exited 0 (a bad emit).
        var cs = EmitZigMulti("""
            const util = @import("util.zig");
            fn f() u8 {
                return 2;
            }
            pub fn main() u8 {
                return util.f() + f();
            }
            """, ("util.zig", "pub fn f() u8 {\n    return 40;\n}\n"));
        cs.ShouldContain("static unsafe byte util__f()");
        cs.ShouldContain("static unsafe byte f()");
        cs.ShouldContain("util__f() + f()");
    }

    [Fact]
    public void A_runtime_string_for_a_comptime_string_parameter_is_rejected()
    {
        var ex = Should.Throw<Exception>(() => EmitZigMulti("""
            const util = @import("util.zig");
            fn pick(s: []const u8) u8 { return util.lenOf(s); }
            pub fn main() u8 { return pick("abc"); }
            """, ("util.zig", Util)));
        ex.Message.ShouldContain("the `comptime s` argument must be a compile-time-known string");
    }

    [Fact]
    public void A_named_container_const_folds_as_a_comptime_argument()
    {
        // `const N: u8 = 20;` IS comptime-known in zig; it used to be rejected as "must be a
        // compile-time-known integer constant". The global still emits as an ordinary field.
        var cs = EmitZig("""
            const N: u8 = 20;
            fn addN(comptime n: u8, x: u8) u8 { return x + n; }
            pub fn main() u8 { return addN(N, 22); }
            """);
        cs.ShouldContain("addN__20(22)");
        cs.ShouldContain("byte N = 20;");
    }

    [Fact]
    public void A_caller_named_const_folds_as_a_cross_module_type_returning_argument()
    {
        // The type-returning path read a comptime VALUE argument in the template's module, so a
        // caller-scoped `const` was unresolvable there; it is read in the caller now, like a type arg.
        var cs = EmitZigMulti("""
            const list = @import("list.zig");
            const CAP: u8 = 3;
            pub fn main() u8 { const s: list.Store(u8, CAP) = .{ .n = 42 }; return s.n; }
            """, ("list.zig", """
            pub fn Store(comptime T: type, comptime cap: u8) type {
                _ = cap;
                return struct { n: T };
            }
            """));
        cs.ShouldContain("list__Store__u8_3 s");
    }

    [Fact]
    public void A_string_literal_len_excludes_the_nul_sentinel()
    {
        // zig types a string literal `*const [N:0]u8`, so `.len` is N — the lowered `char[N+1]` used to
        // answer N+1, directly and through a binding of one.
        var cs = EmitZig("""
            const G = "hello";
            pub fn main() u8 {
                const s = "abc";
                return @intCast(s.len + G.len + "wxyz".len);
            }
            """);
        cs.ShouldContain("3UL + 5UL + 4UL");
    }
}
