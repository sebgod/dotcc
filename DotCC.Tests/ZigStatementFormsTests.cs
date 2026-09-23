#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for forms the array_list and std.math probes reached (road-to-zig-std G4): a <c>catch |e|
/// switch</c> over a <c>!void</c> whose result nobody binds is a statement switch (its prongs may be void
/// blocks); a <c>test</c> block inside a container body parses and is dropped (std.math.Order's
/// <c>test invert</c>); a <c>const n: comptime_int</c> local folds to its literal. End-to-end in the
/// <c>void_catch_switch_and_member_tests</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigStatementFormsTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigsf-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_catch_switch_over_a_void_error_union_is_a_statement_switch()
    {
        var cs = EmitZig("""
            fn shrink(ok: bool) error{OutOfMemory}!void {
                if (!ok) return error.OutOfMemory;
            }
            fn tryShrink(n: *u8, ok: bool) void {
                shrink(ok) catch |e| switch (e) {
                    error.OutOfMemory => {
                        n.* += 10;
                        return;
                    },
                };
                n.* += 1;
            }
            pub fn main() u8 {
                var n: u8 = 31;
                tryShrink(&n, false);
                tryShrink(&n, true);
                return n;
            }
            """);
        // No value temp: the failure path runs the switch for its effects.
        cs.ShouldNotContain("__cfv");
        cs.ShouldMatch(@"if \(Cond\.B\(__cf\w*\.IsErr\)\)\s*\{[^}]*switch \(e\)");
    }

    [Fact]
    public void A_test_block_inside_a_container_body_is_parsed_and_dropped()
    {
        var cs = EmitZig("""
            const Order = enum {
                lt,
                gt,
                pub fn flip(o: Order) Order { return if (o == .lt) .gt else .lt; }
                test flip { _ = Order.lt.flip(); }
                test "named" {}
                test {}
            };
            const Box = struct { n: u8, test "in a struct" {} };
            const U = union(enum) { a: u8, b: void, test "in a union" {} };
            pub fn main() u8 {
                const b: Box = .{ .n = 41 };
                const u: U = .{ .a = 1 };
                _ = u;
                return b.n + @as(u8, @intFromEnum(Order.lt.flip()));
            }
            """);
        cs.ShouldContain("Order_flip(");
        cs.ShouldNotContain("__zigtest");
    }

    [Fact]
    public void A_comptime_int_local_folds_to_its_literal()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                const step: comptime_int = 3 * 4;
                var x: u8 = 30;
                x += step;
                return x;
            }
            """);
        cs.ShouldContain("x += (byte)(12L);");   // the literal, and no `step` local
        cs.ShouldNotMatch(@"\bstep\s*=");
    }
}
