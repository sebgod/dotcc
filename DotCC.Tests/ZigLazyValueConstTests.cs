#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for a LAZY module's top-level value consts (road-to-zig-std G5, mem.zig's
/// <c>use_vectors_for_comparison</c>): the prepare pass records them raw, and a reference lowers the RHS
/// where it is named (zig evaluates a top-level const's initializer at comptime). Also pins what reaching
/// them needed: a trailing comma closing a prong's case list and <c>@inComptime()</c>. End-to-end in the
/// <c>lazy_value_consts</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigLazyValueConstTests
{
    private static string EmitMulti(string main, string module)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-ziglv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "m.zig"), module);
            var path = Path.Combine(dir, "main.zig");
            File.WriteAllText(path, main);
            return Compiler.EmitCSharp(new[] { path });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_lazy_modules_value_const_chain_lowers_where_it_is_named()
    {
        var cs = EmitMulti("""
            const m = @import("m.zig");
            pub fn main() u8 {
                return m.get();
            }
            """, """
            const Mode = enum { fast, small, tiny };
            const mode: Mode = .fast;
            const wide = switch (mode) {
                .small,
                .tiny,
                => false,
                else => true,
            };
            const use_wide = wide and !false;
            const base: u8 = 40;
            pub fn get() u8 {
                if (!@inComptime() and use_wide) return base + 2;
                return base;
            }
            """);
        cs.ShouldContain("return (byte)(40 + 2);");   // `base`, inlined at its annotation's type
        cs.ShouldContain("m__Mode.fast");             // `mode`, reached through `wide` and `use_wide`
        cs.ShouldContain("Cond.B(false)");            // `!false`; `@inComptime()` is false too
    }

    [Fact]
    public void A_lazy_value_const_that_depends_on_itself_is_a_loud_error()
    {
        var ex = Should.Throw<CompileException>(() => EmitMulti("""
            const m = @import("m.zig");
            pub fn main() u8 {
                return m.get();
            }
            """, """
            const a: u8 = b + 1;
            const b: u8 = a + 1;
            pub fn get() u8 {
                return a;
            }
            """));
        ex.Message.ShouldContain("depends on itself");
    }
}
