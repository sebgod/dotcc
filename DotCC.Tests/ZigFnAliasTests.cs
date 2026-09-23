#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for <c>std.fmt.parseIntWithSign</c>'s shapes (road-to-zig-std G3): a local comptime alias of
/// another module's GENERIC function chosen by a comptime switch, a comptime bool bound from a TYPE
/// comparison, a value <c>if</c> that folds on one, a value switch with a <c>return</c> arm, variadic
/// <c>@max</c> folding, and a lazy module's helper reached from two different bodies (it had been declared
/// into the first body's scope). End-to-end in the <c>fn_alias_and_comptime_values</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigFnAliasTests
{
    private const string Module = """
        pub fn add(comptime T: type, a: T, b: T) (error{Overflow}!T) {
            const r = @addWithOverflow(a, b);
            if (r[1] != 0) return error.Overflow;
            return r[0];
        }
        pub fn sub(comptime T: type, a: T, b: T) (error{Overflow}!T) {
            const r = @subWithOverflow(a, b);
            if (r[1] != 0) return error.Overflow;
            return r[0];
        }
        fn twice(x: u8) u8 {
            return x * 2;
        }
        pub fn first(x: u8) u8 {
            return twice(x);
        }
        pub fn second(x: u8) u8 {
            return twice(x) + 1;
        }
        """;

    private static string Emit(string main)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zigfa-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "m.zig"), Module);
            var path = Path.Combine(dir, "main.zig");
            File.WriteAllText(path, main);
            return Compiler.EmitCSharp(new[] { path });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_comptime_switch_picks_a_generic_function_and_a_call_through_the_alias_instantiates_it()
    {
        var cs = Emit("""
            const m = @import("m.zig");
            fn apply(comptime sign: enum { pos, neg }, a: u8, b: u8) u8 {
                const op = switch (sign) {
                    .pos => m.add,
                    .neg => m.sub,
                };
                return op(u8, a, b) catch 0;
            }
            pub fn main() u8 {
                return apply(.pos, 40, 1) + apply(.neg, 2, 1);
            }
            """);
        cs.ShouldContain("m__add__u8(");
        cs.ShouldContain("m__sub__u8(");
    }

    [Fact]
    public void Comptime_bools_from_type_comparisons_fold_a_value_if_and_variadic_max_folds()
    {
        var cs = Emit("""
            fn pick(comptime T: type, x: T) u8 {
                const is_byte = T == u8;
                if (!is_byte) return 0;
                return if (T == u8) x else 0;
            }
            pub fn main() u8 {
                const n: u8 = @max(3, 7, 5);
                return pick(u8, 35) + n;
            }
            """);
        cs.ShouldContain("byte n = 7;");
        cs.ShouldMatch(@"byte pick__u8\(byte x\)\s*\{\s*return x;");   // both comptime tests folded away
    }

    [Fact]
    public void A_value_switch_with_a_return_arm_and_a_helper_named_from_two_bodies_lower()
    {
        var cs = Emit("""
            const m = @import("m.zig");
            fn digit(c: u8) error{Bad}!u8 {
                const v = switch (c) {
                    '0'...'9' => c - '0',
                    else => return error.Bad,
                };
                return v;
            }
            pub fn main() u8 {
                const d = digit('4') catch 0;
                return d + m.first(1) + m.second(2);
            }
            """);
        cs.ShouldContain("case >= 48 and <= 57:");
        cs.ShouldContain("m__twice(");
    }
}
