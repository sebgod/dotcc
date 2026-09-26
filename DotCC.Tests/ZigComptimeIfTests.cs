#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for two parse gaps real std hit behind declarations the resilient parser skipped
/// (road-to-zig-std S9): a <c>comptime if</c> STATEMENT (<c>mem.zig</c>'s findScalarPos:
/// <c>comptime if (block_x_len &lt; 4) break;</c>) and an anonymous <c>enum { … }</c> TYPE
/// (<c>fmt.zig</c>'s parseIntWithSign: <c>comptime sign: enum { pos, neg }</c>).
/// End-to-end in the <c>comptime_if_anon_enum</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigComptimeIfTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigcif-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_comptime_if_folds_and_executes_its_arm_at_compile_time()
    {
        // The taken arm assigns a `comptime var`, so it runs in the comptime evaluator and emits nothing:
        // lowering it as runtime code stored into the substituted literal (`1 = 20;`, CS0131).
        var cs = EmitZig("""
            fn weight(comptime T: type) u8 {
                comptime var w: u8 = 1;
                comptime if (@sizeOf(T) > 1) {
                    w = 20;
                } else {
                    w = 2;
                };
                return w;
            }
            pub fn main() u8 {
                return weight(u32) + weight(u8);
            }
            """);
        cs.ShouldContain("return 20;");
        cs.ShouldContain("return 2;");
        cs.ShouldNotContain("1 = 20;");
    }

    [Fact]
    public void A_block_bodied_comptime_if_is_accepted_without_the_trailing_semicolon()
    {
        // zig requires the `;` (it is an expression statement); dotcc accepts both spellings.
        var cs = EmitZig("""
            fn pick(comptime n: u8) u8 {
                comptime var r: u8 = 0;
                comptime if (n > 3) {
                    r = 42;
                }
                return r;
            }
            pub fn main() u8 {
                return pick(9);
            }
            """);
        cs.ShouldContain("return 42;");
    }

    [Fact]
    public void A_comptime_if_whose_condition_is_not_comptime_known_is_rejected()
    {
        var ex = Should.Throw<CompileException>(() => EmitZig("""
            fn f(x: u8) u8 {
                comptime if (x > 3) {};
                return x;
            }
            pub fn main() u8 {
                return f(42);
            }
            """));
        ex.Message.ShouldContain("`comptime if`: the condition is not known at compile time");
    }

    [Fact]
    public void An_anonymous_enum_parameter_type_is_one_enum_shared_by_every_instance()
    {
        // The enum is reified once per SOURCE site, so both instances of `sign` name the same type, and a
        // bare `.pos` argument resolves against the parameter's type.
        var cs = EmitZig("""
            fn sign(comptime s: enum { pos, neg }, x: u8) u8 {
                return switch (s) {
                    .pos => x,
                    .neg => 0,
                };
            }
            pub fn main() u8 {
                return sign(.pos, 42) + sign(.neg, 7);
            }
            """);
        cs.ShouldContain("enum __AnonEnum0");
        cs.ShouldNotContain("__AnonEnum1");
        cs.ShouldContain("sign__0(42)");
        cs.ShouldContain("sign__1(7)");
    }
}
