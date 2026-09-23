#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for std.Io.Writer.print's comptime format SCAN (road-to-zig-std G3, the format engine's first
/// brick): a comptime string indexed by a comptime var is a byte, a <c>switch</c> over a comptime value
/// inside an unrolled loop selects its prong, a <c>break</c> / <c>continue</c> that is comptime control ends
/// or continues the unrolling, a bare <c>inline while (true)</c> unrolls, a compound continue-expression
/// (<c>: (i += 1)</c>, previously treated as <c>= 1</c>) steps, and an assignment to a <c>comptime var</c>
/// runs at lowering time. End-to-end in the <c>comptime_format_scan</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigComptimeScanTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigcs-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_comptime_scan_over_a_comptime_string_folds_to_its_result()
    {
        var cs = EmitZig("""
            fn countBraces(comptime fmt: []const u8) u8 {
                comptime var i = 0;
                comptime var n = 0;
                inline while (true) {
                    inline while (i < fmt.len) : (i += 1) {
                        switch (fmt[i]) {
                            '{', '}' => break,
                            else => {},
                        }
                    }
                    if (i >= fmt.len) break;
                    n += 1;
                    i += 1;
                }
                return n;
            }
            pub fn main() u8 {
                return countBraces("a{}b{}c{") * 8 + 2;
            }
            """);
        // The whole scan folds away: no loop, no switch, just the count.
        cs.ShouldMatch(@"countBraces__s[0-9a-f]+\(\)\s*\{[\s{}]*return 5;");
    }

    [Fact]
    public void A_runtime_conditional_break_in_an_inline_while_stays_a_loud_cut()
    {
        var ex = Should.Throw<CompileException>(() => EmitZig("""
            pub fn main() u8 {
                var x: u8 = 3;
                x += 0;
                comptime var i = 0;
                inline while (i < 4) : (i += 1) {
                    if (x == i) break;
                }
                return 42;
            }
            """));
        ex.Message.ShouldContain("not comptime control flow");
    }
}
