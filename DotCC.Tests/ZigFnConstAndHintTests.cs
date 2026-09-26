#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for forms std.HashMap's auto context and hashing reach (road-to-zig-std G4): a container CONST
/// bound to a closure-idiom function value called as a method (<c>pub const hash = getAutoHashFn(K, @This());</c>
/// then <c>ctx.hash(key)</c>), a closure-idiom body opening with a <c>comptime { … }</c> guard (an <c>if</c>
/// with a <c>@compileError</c>), a <c>switch</c> as the right operand of <c>and</c>, <c>@branchHint</c>, and the
/// curated <c>std.mem.Alignment</c>'s <c>fromByteUnits</c> / <c>forward</c> / <c>toByteUnits</c>. End-to-end in
/// the <c>container_fn_consts_and_hints</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigFnConstAndHintTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigfc-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private const string Doubler = """
        fn getDoubler(comptime K: type, comptime Context: type) (fn (Context, K) u64) {
            comptime {
                if (K == []const u8) @compileError("no slices");
            }
            return struct {
                fn run(ctx: Context, key: K) u64 {
                    _ = ctx;
                    return @as(u64, key) * 2;
                }
            }.run;
        }
        fn Ctx(comptime K: type) type {
            return struct { pub const hash = getDoubler(K, @This()); };
        }
        """;

    [Fact]
    public void A_container_const_bound_to_a_function_value_is_callable_as_a_method()
    {
        var cs = EmitZig(Doubler + """
            pub fn main() u8 {
                const c: Ctx(u32) = .{};
                return @intCast(c.hash(20) + Ctx(u32).hash(c, 1));
            }
            """);
        cs.ShouldContain("getDoubler__u32_Ctx__u32__Anon_run(c, 20)");
        cs.ShouldContain("getDoubler__u32_Ctx__u32__Anon_run(c, 1)");
    }

    [Fact]
    public void A_compileError_in_a_taken_comptime_block_branch_is_raised()
    {
        var ex = Should.Throw<CompileException>(() => EmitZig(Doubler + """
            pub fn main() u8 {
                const c: Ctx([]const u8) = .{};
                return @intCast(c.hash("ab"));   // referenced: the guard is analysed, and fires
            }
            """));
        ex.Message.ShouldContain("no slices");
    }

    [Fact]
    public void A_switch_is_an_and_operand_and_a_branch_hint_emits_nothing()
    {
        var cs = EmitZig("""
            const Size = enum { one, many, slice };
            fn unique(one: bool, size: Size) bool {
                return one and switch (size) {
                    .one, .many => true,
                    .slice => false,
                };
            }
            pub fn main() u8 {
                if (unique(true, .many)) {
                    @branchHint(.likely);
                    return 42;
                }
                return 0;
            }
            """);
        cs.ShouldMatch(@"Cond\.B\(one\) && Cond\.B\(\(\(\(int\)size\) switch");
        cs.ShouldNotContain("branchHint");
    }

    [Fact]
    public void The_curated_Alignment_constructs_from_byte_units_and_rounds_forward()
    {
        var cs = EmitZig("""
            const std = @import("std");
            pub fn main() u8 {
                const a: std.mem.Alignment = comptime .fromByteUnits(8);
                return @intCast(a.forward(13) + a.toByteUnits() + 18);
            }
            """);
        cs.ShouldContain("Alignment a = Alignment.fromByteUnits(8);");
        cs.ShouldContain("Alignment.Forward(a, 13)");
        cs.ShouldContain("Alignment.ToByteUnits(a)");
    }
}
