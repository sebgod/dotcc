#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for a RUNTIME multi-object <c>for</c> (road-to-zig-std G5, hash_map's
/// <c>for (metadata, keys, values) |m, k, v|</c>): one index walks every object in lockstep, each capture
/// a per-iteration copy. Also pins an assignment prong body (<c>.stage2_llvm =&gt; _ = &amp;dbHelper,</c>) and
/// the diagnostic that a type-position call reached through a same-module ALIAS of a declaration the
/// resilient parse skipped raises that parse error. End-to-end in the <c>multi_object_for</c> zig-oracle
/// program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigMultiObjectForTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigmf-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Three_objects_walk_in_lockstep_and_a_trailing_comma_parses()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                const used = [_]bool{ true, false, true };
                const keys = [_]u8{ 1, 2, 3 };
                var vals = [_]u8{ 10, 20, 30 };
                var total: u8 = 0;
                for (
                    &used,
                    &keys,
                    vals[0..],
                ) |m, k, v| {
                    if (!m) continue;
                    total += k + v;
                }
                return total;
            }
            """);
        cs.ShouldContain("__i < __s.Len");
        cs.ShouldContain("CBool m = __s.Ptr[__i];");
        cs.ShouldContain("byte k = ");
        cs.ShouldContain("byte v = ");
    }

    [Fact]
    public void An_assignment_prong_body_lowers_as_a_statement()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                var bonus: u8 = 0;
                var n: u8 = 3;
                n += 0;
                switch (n) {
                    3 => bonus = 42,
                    else => _ = &bonus,
                }
                return bonus;
            }
            """);
        cs.ShouldContain("bonus = 42;");
    }

    [Fact]
    public void A_type_call_through_an_alias_of_a_skipped_declaration_raises_its_parse_error()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zigmf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "maps.zig"), """
                pub const Map = Custom;
                fn Custom(comptime K: type) type {
                    const broken = ;
                    return struct { k: K };
                }
                """);
            var main = Path.Combine(dir, "main.zig");
            File.WriteAllText(main, """
                const maps = @import("maps.zig");
                pub fn main() u8 {
                    const m: maps.Map(u8) = .{ .k = 42 };
                    return m.k;
                }
                """);
            var ex = Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { main }));
            ex.Message.ShouldContain("zig `Custom` in maps.zig did not parse");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
