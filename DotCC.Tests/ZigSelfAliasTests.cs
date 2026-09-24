#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for a ROOT file naming itself (road-to-zig-std, task #56): <c>const root = @This();</c>, then
/// <c>root.helper()</c>, <c>root.Point</c>, <c>root.limit</c> and <c>&amp;root.helper</c>. A std file's self alias
/// (<c>const mem = @This();</c>) resolves through its own module; the root has none, so it gets a synthetic one over
/// its own lowering. A module's top-level function is also usable as a VALUE through any module path
/// (<c>const f = util.helper;</c>), which the root alias needed too. End to end in the <c>root_self_alias</c>
/// zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigSelfAliasTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigself-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private const string Program = """
        const root = @This();

        const limit: u8 = 3;

        fn helper(x: u8) u8 {
            return x + 1;
        }

        const Point = struct {
            x: u8,
            pub fn sum(self: Point) u8 {
                return self.x + root.limit;
            }
        };

        fn twice(f: *const fn (u8) u8, x: u8) u8 {
            return f(f(x));
        }

        pub fn main() u8 {
            const p: root.Point = .{ .x = 4 };
            const f = root.helper;
            const via_type = root.Point.sum(p);
            return root.helper(p.sum()) + twice(f, via_type) + twice(&root.helper, 1);
        }
        """;

    [Fact]
    public void A_root_self_alias_names_the_files_own_functions_types_and_consts()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("Point p = new Point { x = 4 };");
        cs.ShouldContain("byte via_type = Point_sum(p);");
        cs.ShouldContain("return (byte)(self.x + 3);");
        cs.ShouldContain("return (byte)(helper(Point_sum(p)) + twice(f, via_type) + twice(&helper, 1));");
    }

    [Fact]
    public void A_root_self_alias_is_comptime_only_and_emits_no_global()
    {
        var cs = EmitZig(Program);
        cs.ShouldNotMatch(@"\broot\s*=");
    }

    [Fact]
    public void A_function_named_through_a_root_self_alias_is_a_value()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("delegate*<byte, byte> f = &helper;");
    }

    [Fact]
    public void An_imported_modules_function_is_a_value()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zigfnval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "util.zig"), "pub fn helper(x: u8) u8 { return x + 1; }\n");
            var main = Path.Combine(dir, "main.zig");
            File.WriteAllText(main, """
                const util = @import("util.zig");
                fn twice(f: *const fn (u8) u8, x: u8) u8 { return f(f(x)); }
                pub fn main() u8 { const f = util.helper; return f(1) + twice(&util.helper, 1); }
                """);
            var cs = Compiler.EmitCSharp(new[] { main });
            cs.ShouldContain("delegate*<byte, byte> f = &util__helper;");
            cs.ShouldContain("return (byte)(f(1) + twice(&util__helper, 1));");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
