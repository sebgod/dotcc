#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for jumps out of a <c>switch</c> prong (road-to-zig-std G3, <c>std.Io.Writer.print</c>'s
/// <c>'{', '}' =&gt; break,</c>). zig's unlabeled <c>break</c> in a prong exits the enclosing LOOP, but a
/// prong lowers into a C# <c>switch</c>, where <c>break</c> exits only the switch: that was a silent
/// miscompile (the loop kept iterating), now a <c>goto</c> past the loop. Also pins the bare jump prong
/// bodies <c>=&gt; break</c>, <c>=&gt; continue [:l]</c>, <c>=&gt; break :blk v</c>, in a statement switch and
/// in <c>catch |e| switch (e)</c>. End-to-end in the <c>switch_break_exits_loop</c> and
/// <c>switch_jump_prongs</c> zig-oracle programs.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigSwitchJumpTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigsj-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_break_in_a_switch_prong_exits_the_enclosing_loop()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                var i: u8 = 0;
                while (i < 10) : (i += 1) {
                    switch (i) {
                        3 => {
                            break;
                        },
                        else => {},
                    }
                }
                return i;
            }
            """);
        cs.ShouldContain("goto __loop0_swbrk;");
        cs.ShouldContain("__loop0_swbrk:");
    }

    [Fact]
    public void A_break_outside_any_switch_stays_a_plain_break()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                var i: u8 = 0;
                while (i < 10) : (i += 1) {
                    if (i == 3) break;
                }
                return i;
            }
            """);
        cs.ShouldNotContain("_swbrk");
        cs.ShouldContain("break;");
    }

    [Fact]
    public void Bare_jump_prong_bodies_parse_and_lower()
    {
        var cs = EmitZig("""
            fn f(x: u8) error{ A, B }!u8 {
                if (x == 3) return error.A;
                if (x == 1) return error.B;
                return x;
            }
            pub fn main() u8 {
                var i: u8 = 0;
                outer: while (i < 20) : (i += 1) {
                    switch (i) {
                        1 => continue,
                        2 => continue :outer,
                        7 => break,
                        else => {},
                    }
                }
                const v: u8 = blk: {
                    switch (i) {
                        7 => break :blk 24,
                        else => break :blk 0,
                    }
                };
                var k: u8 = 0;
                while (k < 10) : (k += 1) {
                    const x = f(k) catch |e| switch (e) {
                        error.A => break,
                        error.B => continue,
                    };
                    _ = x;
                }
                return i + v + k;
            }
            """);
        cs.ShouldContain("goto __loop0_cont;");      // `continue :outer`
        cs.ShouldContain("goto __loop0_brk;");       // `break` out of the labeled loop, through the switch
        cs.ShouldContain("__blk0 = 24;");            // `break :blk 24`
        cs.ShouldContain("goto __loop1_swbrk;");     // `break` in the `catch |e| switch`
    }

    [Fact]
    public void A_nested_switch_prong_body_is_a_statement_whose_prongs_may_return()
    {
        // std.math.sqrt: `.int => |I| switch (I.signedness) { .signed => @compileError(…), .unsigned => return … }`.
        var cs = EmitZig("""
            fn pick(x: u8) u8 {
                switch (x) {
                    0 => switch (x + 1) {
                        1 => return 2,
                        else => {},
                    },
                    else => {},
                }
                return 0;
            }
            pub fn main() u8 {
                return pick(0) + 40;
            }
            """);
        cs.ShouldMatch(@"case 0:\s*switch");
        cs.ShouldContain("return 2;");
    }
}
