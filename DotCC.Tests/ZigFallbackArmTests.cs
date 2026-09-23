#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for STATEMENT-shaped <c>catch</c> / <c>orelse</c> fallbacks (road-to-zig-std): the failure
/// path runs a statement — <c>break [:l [v]]</c>, <c>continue [:l]</c>, a <c>{ … }</c> block,
/// <c>catch |e| return e</c> — or yields through a <c>switch</c> whose prongs are values or jumps. ~1,000
/// sites in the pinned std (<c>catch |err| switch (err)</c> alone is 498), each of which used to be a parse
/// error that made the resilient parse drop the whole enclosing function — <c>std.fmt.bufPrint</c> among
/// them. One arm abstraction flows through the three existing positions (a declaration initializer, an
/// expression statement / <c>_ =</c> discard, and the ANF sub-expression hoist). End-to-end in the
/// <c>fallback_arms</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigFallbackArmTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigfb-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private const string Prelude = """
        const E = error{ Bad, Worse };
        fn parse(x: u8) E!u8 {
            if (x == 0) return error.Bad;
            if (x == 1) return error.Worse;
            return x;
        }
        fn lookup(x: u8) ?u8 {
            return if (x > 5) x else null;
        }

        """;

    [Fact]
    public void Catch_capture_switch_yields_a_value_or_returns_the_error()
    {
        // The 498-site shape, incl. the `else => |e| return e` capture prong that ends most of them.
        var cs = EmitZig(Prelude + """
            fn soften(x: u8) E!u8 {
                return parse(x) catch |err| switch (err) {
                    error.Bad => 30,
                    else => |e| return e,
                };
            }
            pub fn main() u8 { return soften(0) catch 0; }
            """);
        cs.ShouldContain("ushort err = __cf");          // the capture binds the error code
        cs.ShouldContain("ErrUnion<byte>.Err(e)");      // `return e` is an ERROR return of the runtime code
        cs.ShouldNotContain("ErrUnion<byte>.Ok(e)");
    }

    [Fact]
    public void Returning_a_captured_error_is_an_error_return()
    {
        // Previously `return e;` wrapped the error code as the SUCCESS payload.
        var cs = EmitZig(Prelude + """
            fn pass(x: u8) E!u8 {
                const v = parse(x) catch |e| return e;
                return v + 1;
            }
            pub fn main() u8 { return pass(1) catch 2; }
            """);
        cs.ShouldContain("ErrUnion<byte>.Err(e)");
    }

    [Fact]
    public void Catch_and_orelse_blocks_that_never_fall_through_bind_the_payload()
    {
        var cs = EmitZig(Prelude + """
            fn orDefault(x: u8) u8 {
                const v = parse(x) catch {
                    return 7;
                };
                const w = lookup(v) orelse {
                    return 3;
                };
                return w;
            }
            pub fn main() u8 { return orDefault(9); }
            """);
        cs.ShouldContain("return 7;");
        cs.ShouldContain("return 3;");
        cs.ShouldContain("byte w = __cf");
    }

    [Fact]
    public void Orelse_break_and_continue_jump_out_of_the_loop()
    {
        var cs = EmitZig(Prelude + """
            pub fn main() u8 {
                var total: u8 = 0;
                var i: u8 = 0;
                while (i < 10) : (i += 1) {
                    const v = lookup(i) orelse continue;
                    if (v == 9) {
                        _ = lookup(0) orelse break;
                    }
                    total += v;
                }
                return total;
            }
            """);
        cs.ShouldContain("continue;");
        cs.ShouldContain("break;");
    }

    [Fact]
    public void A_labeled_break_with_a_value_fills_the_labeled_block()
    {
        var cs = EmitZig(Prelude + """
            pub fn main() u8 {
                const found = blk: {
                    const x = lookup(8) orelse break :blk 0;
                    break :blk x;
                };
                return found;
            }
            """);
        cs.ShouldContain("goto ");   // the labeled value-block's exit
    }

    [Fact]
    public void A_discarded_catch_block_may_fall_through()
    {
        // `_ = a catch {};` — the value is discarded, so a void block is fine (as in zig).
        var cs = EmitZig(Prelude + """
            pub fn main() u8 {
                _ = parse(0) catch {};
                return 42;
            }
            """);
        cs.ShouldContain("__cf");
    }

    [Fact]
    public void A_fallback_block_that_falls_through_where_a_value_is_needed_is_rejected()
    {
        var ex = Should.Throw<Exception>(() => EmitZig(Prelude + """
            pub fn main() u8 {
                const v = lookup(1) orelse {
                    var q: u8 = 0;
                    q += 1;
                };
                return v;
            }
            """));
        ex.Message.ShouldContain("must not fall through");
    }

    [Fact]
    public void A_fallback_arm_in_a_sub_expression_is_hoisted()
    {
        // The ANF hoist path: the arm runs before the enclosing statement, the payload is a temp.
        var cs = EmitZig(Prelude + """
            fn add(a: u8, b: u8) u8 { return a + b; }
            pub fn main() u8 {
                var i: u8 = 0;
                var total: u8 = 0;
                while (i < 10) : (i += 1) {
                    total = add(total, lookup(i) orelse continue);
                }
                return total;
            }
            """);
        cs.ShouldContain("__anf");
    }
}
