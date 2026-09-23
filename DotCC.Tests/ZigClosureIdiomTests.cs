#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for the CLOSURE idiom and COMPTIME function parameters (road-to-zig-std G3, std.mem.sort's
/// <c>comptime lessThanFn: fn (@TypeOf(context), lhs: T, rhs: T) bool</c> given <c>std.sort.asc(u8)</c>):
/// <c>return struct { pub fn inner … }.inner;</c> makes the instance stand for its method, a comptime
/// function argument keys the callee's instance and is called directly, and the idiom also works in
/// expression position. Also pins the parse bricks std.sort.block needed on the way: an inline
/// <c>@import(…).name</c> re-export, <c>noalias</c>, by-reference pair captures and a <c>comptime { … }</c>
/// prong. End-to-end in the <c>closure_idiom_fn_params</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigClosureIdiomTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigci-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_closure_idiom_call_is_a_comptime_function_argument_called_directly()
    {
        var cs = EmitZig("""
            fn asc(comptime T: type) fn (void, T, T) bool {
                return struct {
                    pub fn inner(_: void, a: T, b: T) bool {
                        return a < b;
                    }
                }.inner;
            }
            fn first(comptime T: type, items: []T, context: anytype, comptime lessThan: fn (@TypeOf(context), T, T) bool) T {
                return if (lessThan(context, items[1], items[0])) items[1] else items[0];
            }
            pub fn main() u8 {
                var a = [_]u8{ 43, 42 };
                return first(u8, &a, {}, asc(u8));
            }
            """);
        cs.ShouldContain("first__u8_void_asc__u8__Anon_inner(");
        cs.ShouldContain("asc__u8__Anon_inner(items.Ptr[1], items.Ptr[0])");
        cs.ShouldContain("static unsafe CBool asc__u8__Anon_inner(byte a, byte b)");
    }

    [Fact]
    public void The_closure_idiom_in_expression_position_is_a_function_value()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                const inc = struct {
                    fn f(x: u8) u8 {
                        return x + 1;
                    }
                }.f;
                return inc(41);
            }
            """);
        cs.ShouldContain("main__L0__Anon_f");
    }

    [Fact]
    public void Noalias_and_by_reference_pair_captures_parse_and_lower()
    {
        var cs = EmitZig("""
            fn swap(noalias a: *u8, noalias b: *u8) void {
                const t = a.*;
                a.* = b.*;
                b.* = t;
            }
            pub fn main() u8 {
                var xs = [_]u8{ 1, 2 };
                const ys = [_]u8{ 10, 20 };
                for (&xs, ys) |*x, y| x.* += y;
                var p: u8 = 40;
                var q: u8 = 0;
                swap(&p, &q);
                return q + xs[0] - 9;
            }
            """);
        cs.ShouldContain("static unsafe void swap(byte* a, byte* b)");
        cs.ShouldContain("byte* x = &__s.Ptr[__i];");
    }

    [Fact]
    public void An_inline_import_re_export_resolves()
    {
        // sort.zig's `pub const block = @import("sort/block.zig").block;`.
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zigci-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "sub", "impl.zig"), "pub fn answer() u8 {\n    return 42;\n}\n");
            File.WriteAllText(Path.Combine(dir, "api.zig"), "pub const answer = @import(\"sub/impl.zig\").answer;\n");
            var main = Path.Combine(dir, "main.zig");
            File.WriteAllText(main, "const api = @import(\"api.zig\");\npub fn main() u8 {\n    return api.answer();\n}\n");
            var cs = Compiler.EmitCSharp(new[] { main });
            cs.ShouldContain("return impl__answer();");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
