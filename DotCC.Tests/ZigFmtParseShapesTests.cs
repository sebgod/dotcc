#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for the shapes std.fmt.Placeholder.parse and std.fmt.Parser need (road-to-zig-std, the bufPrint path):
/// a static call through a type ANOTHER lazy module declares, whose body names that module's own types; a void
/// variant built as <c>.{ .none = {} }</c>; a value switch over a union with a <c>|v|</c> capture prong;
/// <c>x catch unreachable</c> in value position; an untyped top-level <c>const default_mode = .right;</c> read
/// at a sink. End-to-end in the <c>fmt_parse_shapes</c> multi-file zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigFmtParseShapesTests
{
    private static string EmitZigMulti(string mainSource, params (string name, string source)[] siblings)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zigfps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var mainPath = Path.Combine(dir, "main.zig");
        File.WriteAllText(mainPath, mainSource);
        foreach (var (name, source) in siblings) { File.WriteAllText(Path.Combine(dir, name), source); }
        try { return Compiler.EmitCSharp(new[] { mainPath }); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_lazy_module_calls_a_static_method_through_another_modules_type()
    {
        var cs = EmitZigMulti("""
            const a = @import("a.zig");
            pub fn main() u8 {
                return a.go();
            }
            """,
            ("a.zig", """
                const b = @import("b.zig");
                pub fn go() u8 {
                    return b.T.make();
                }
                """),
            ("b.zig", """
                const P = struct { v: u8 };
                pub const T = struct {
                    pub fn make() u8 {
                        const p: P = .{ .v = 42 };
                        return p.v;
                    }
                };
                """));
        // The method lowers in b.zig, where its body resolves b's own `P`.
        cs.ShouldMatch(@"b__P p = new b__P \{ v = 42 \};");
    }

    [Fact]
    public void A_void_variant_takes_the_void_value_and_a_capture_prong_fills_a_value_switch()
    {
        var cs = EmitZigMulti("""
            const s = @import("s.zig");
            pub fn main() u8 {
                return s.total(s.make(false)) + s.total(s.make(true));
            }
            """,
            ("s.zig", """
                pub const Spec = union(enum) { none, number: u8 };
                pub fn make(n: bool) Spec {
                    if (n) return .{ .number = 10 };
                    return .{ .none = {} };
                }
                pub fn total(sp: Spec) u8 {
                    const t: u8 = switch (sp) {
                        .none => 1,
                        .number => |v| v * 4,
                    };
                    return t;
                }
                """));
        cs.ShouldNotContain("is a void variant");
        cs.ShouldMatch(@"byte v = [^;]*__payload\.number;");
    }

    [Fact]
    public void Catch_unreachable_in_value_position_renders_a_throw_expression()
    {
        var cs = EmitZigMulti("""
            const s = @import("s.zig");
            pub fn main() u8 {
                return s.get();
            }
            """,
            ("s.zig", """
                fn f() !u8 {
                    return 42;
                }
                pub fn get() u8 {
                    const v = f() catch unreachable;
                    return v;
                }
                """));
        cs.ShouldMatch(@"\? throw new System\.Diagnostics\.UnreachableException\(""unreachable\(\) reached""\) : ");
    }
}
