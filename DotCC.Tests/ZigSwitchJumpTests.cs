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
    [Fact]
    public void A_labeled_switch_continue_runs_the_switch_again_on_its_operand()
    {
        // GH #286 (zig 0.14, std.zig.Tokenizer's state machine): the operand is a temp the switch reads, and `continue
        // :state .x` assigns it and jumps back to a label before the switch.
        var cs = EmitZig("""
            const State = enum { start, ident, done };
            fn run(s: []const u8) u8 {
                var i: usize = 0;
                var n: u8 = 0;
                state: switch (State.start) {
                    .start => {
                        if (i >= s.len) continue :state .done;
                        continue :state .ident;
                    },
                    .ident => {
                        i += 1;
                        n += 1;
                        continue :state .start;
                    },
                    .done => {},
                }
                return n;
            }
            pub fn main() u8 { return run("abc"); }
            """);
        cs.ShouldContain("State __lsw0 = State.start;");
        cs.ShouldContain("__lsw0_top:");
        System.Text.RegularExpressions.Regex.IsMatch(cs, @"__lsw0 = State\.done;\s+goto __lsw0_top;").ShouldBeTrue(cs);
    }

    [Fact]
    public void A_bare_prong_value_hoists_inside_its_prong()
    {
        // GH #286: `0 => a orelse return 9` in a labeled value switch hoists its early return into its own prong; the
        // statement holding the switch had taken it, ahead of the whole switch.
        var cs = EmitZig("""
            fn h(x: u8, a: ?u8) u8 {
                const r: u8 = sw: switch (x) {
                    0 => a orelse return 9,
                    1 => break :sw 3,
                    else => 2,
                };
                return r;
            }
            pub fn main() u8 { return h(1, null); }
            """);
        System.Text.RegularExpressions.Regex.IsMatch(cs, @"case 0:\s+if \(!Cond\.B\(a\.HasValue\)\)\s+\{\s+return 9;").ShouldBeTrue(cs);
        cs.ShouldNotContain("goto case 1;");   // the prong ends in its break to the label; no dead fall-through after it
    }

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
