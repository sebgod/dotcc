#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for zig DECL LITERALS (zig 0.14+, road-to-zig-std G3 — <c>var w: Writer = .fixed(buf);</c>
/// in <c>std.fmt.bufPrint</c>): a <c>.name(args)</c> call or a bare <c>.name</c> at a struct/union
/// result location is the member <c>name</c> of the RESULT type — <c>T.name(args)</c> / <c>T.name</c> —
/// the same lookup an enum literal's tag gets. Every sink kind reaches it (a typed decl, a parameter, a
/// <c>return</c>), and a container another module declares resolves through the shared method registry.
/// End-to-end in the <c>decl_literals</c> and <c>import_decl_literal</c> zig-oracle programs.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigDeclLiteralTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigdl-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private static string EmitZigMulti(string mainSource, params (string name, string source)[] siblings)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zigdl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var mainPath = Path.Combine(dir, "main.zig");
        File.WriteAllText(mainPath, mainSource);
        foreach (var (name, source) in siblings) { File.WriteAllText(Path.Combine(dir, name), source); }
        try { return Compiler.EmitCSharp(new[] { mainPath }); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private const string Counter = """
        const Counter = struct {
            n: u8,
            pub const zero: Counter = .{ .n = 0 };
            pub fn init(n: u8) Counter { return .{ .n = n }; }
            pub fn fixed() Counter { return .{ .n = 2 }; }
        };
        """;

    [Fact]
    public void A_decl_literal_call_at_a_typed_decl_is_the_associated_call()
    {
        var cs = EmitZig(Counter + """
            pub fn main() u8 {
                const a: Counter = .init(40);
                const b: Counter = .fixed();
                return a.n + b.n;
            }
            """);
        cs.ShouldContain("Counter a = Counter_init(40);");
        cs.ShouldContain("Counter b = Counter_fixed();");
    }

    [Fact]
    public void A_decl_literal_resolves_at_a_parameter_and_a_return()
    {
        var cs = EmitZig(Counter + """
            fn make() Counter { return .init(30); }
            fn take(c: Counter) u8 { return c.n; }
            pub fn main() u8 { return take(.init(12)) + make().n; }
            """);
        cs.ShouldContain("return Counter_init(30);");
        cs.ShouldContain("take(Counter_init(12))");
    }

    [Fact]
    public void A_decl_literal_value_is_the_container_const()
    {
        var cs = EmitZig(Counter + """
            pub fn main() u8 { const z: Counter = .zero; return z.n; }
            """);
        // The const's RHS is re-lowered at the use, as `Counter.zero` would be.
        cs.ShouldContain("Counter z = new Counter");
    }

    [Fact]
    public void A_decl_literal_on_an_imported_container_lowers_in_its_module()
    {
        var cs = EmitZigMulti("""
            const geom = @import("geom.zig");
            pub fn main() u8 { const p: geom.Point = .init(40, 2); return p.sum(); }
            """, ("geom.zig", """
            pub const Point = struct {
                x: u8,
                y: u8,
                pub fn init(x: u8, y: u8) Point { return .{ .x = x, .y = y }; }
                pub fn sum(self: Point) u8 { return self.x + self.y; }
            };
            """));
        cs.ShouldContain("geom__Point p = geom__Point_init(40, 2);");
    }

    [Fact]
    public void A_decl_literal_naming_a_missing_function_is_rejected()
    {
        var ex = Should.Throw<Exception>(() => EmitZig(Counter + """
            pub fn main() u8 { const a: Counter = .nope(1); return a.n; }
            """));
        ex.Message.ShouldContain("decl literal `.nope(…)`: 'Counter' has no function 'nope'");
    }

    [Fact]
    public void A_decl_literal_call_without_a_result_type_is_rejected()
    {
        var ex = Should.Throw<Exception>(() => EmitZig(Counter + """
            pub fn main() u8 { const a = .init(1); return a.n; }
            """));
        ex.Message.ShouldContain("decl literal `.init(…)` needs a struct/union result type");
    }
}
