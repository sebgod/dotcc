#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for the parse gaps that kept <c>std.array_list.Aligned</c> (what <c>std.ArrayList</c> is) from
/// parsing at all (road-to-zig-std S9/G4), each measured on real std, plus the sibling type-returning call
/// behind <c>std.AutoHashMap</c>: <c>align(E)</c> in pointer and slice types (561 uses), a general sentinel
/// in a type and in a slicing expression (77 + 73), a trailing comma in a call (zig fmt's multi-line
/// layout) and <c>inline fn</c> (379). End-to-end in the <c>std_parse_bricks</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigStdParseBricksTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigspb-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Aligned_pointer_and_slice_types_lower_as_their_unaligned_forms()
    {
        // C# pointers carry no alignment, so `align(E)` is parsed and not tracked (as on a declaration).
        var cs = EmitZig("""
            fn first(s: []align(1) const u8, p: *align(4) const u8, m: [*]align(2) u8) u8 {
                return s[0] + p.* + m[0];
            }
            pub fn main() u8 {
                var buf = [_]u8{ 40, 1, 1 };
                return first(&buf, &buf[1], &buf);
            }
            """);
        cs.ShouldContain("static unsafe byte first(ConstSlice<byte> s, byte* p, byte* m)");
    }

    [Fact]
    public void A_general_sentinel_types_and_slices_like_the_zero_sentinel()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                const buf = [_]u8{ 40, 2, 3 };
                const s: [:3]const u8 = buf[0..2 :3];
                const t: [:3]const u8 = buf[1.. :3];
                return s[0] + t[0];
            }
            """);
        cs.ShouldContain("ConstSlice<byte> s =");
        cs.ShouldContain("ConstSlice<byte> t =");
    }

    [Fact]
    public void A_trailing_comma_in_a_call_and_inline_functions_parse()
    {
        var cs = EmitZig("""
            const Box = struct {
                v: u8,
                pub inline fn get(self: Box) u8 {
                    return self.v;
                }
            };
            inline fn add(a: u8, b: u8) u8 {
                return a + b;
            }
            pub fn main() u8 {
                const b = Box{ .v = add(
                    40,
                    2,
                ) };
                return b.get();
            }
            """);
        cs.ShouldContain("static unsafe byte add(byte a, byte b)");
        cs.ShouldContain("static unsafe byte Box_get(Box self)");
        cs.ShouldContain("add(40, 2)");
    }

    [Fact]
    public void A_sibling_type_returning_generic_in_an_imported_module_is_declared_on_demand()
    {
        // hash_map.zig's `AutoHashMap` body names its sibling `HashMap(…)` by bare name; a lazy module had
        // not declared it yet, so the type-position call fell through ("zig type: CallArgs").
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zigspb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "maps.zig"), """
                pub fn Map(comptime K: type) type {
                    return Inner(K, u8);
                }
                fn Inner(comptime K: type, comptime V: type) type {
                    return struct { k: K, v: V };
                }
                """);
            var main = Path.Combine(dir, "main.zig");
            File.WriteAllText(main, """
                const maps = @import("maps.zig");
                pub fn main() u8 {
                    const m: maps.Map(u8) = .{ .k = 40, .v = 2 };
                    return m.k + m.v;
                }
                """);
            var cs = Compiler.EmitCSharp(new[] { main });
            cs.ShouldContain("maps__Inner__u8_u8 m =");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
