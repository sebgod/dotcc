#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for TYPE const members of a reified struct (road-to-zig-std G4/G5): both big std containers
/// declare the types of their own fields as members of the returned struct,
/// <c>pub const Slice = if (alignment) |a| ([]align(…) T) else []T;</c> (<c>array_list.Aligned</c>) and
/// <c>pub const Unmanaged = HashMapUnmanaged(K, V, …);</c> (<c>HashMap</c>). Such a member is evaluated with
/// the instantiation's comptime seeds live, so a field typed by it resolves per instance.
/// End-to-end in the <c>reified_type_consts</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigReifiedTypeConstTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigrtc-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_type_const_member_types_a_field_per_instantiation()
    {
        var cs = EmitZig("""
            fn Inner(comptime K: type, comptime V: type) type {
                return struct { k: K, v: V };
            }
            fn Outer(comptime K: type, comptime cap: ?usize) type {
                return struct {
                    pair: Pair,
                    items: Items,
                    pub const Pair = Inner(K, u8);
                    pub const Items = if (cap) |_| []const K else []const u8;
                };
            }
            pub fn main() u8 {
                const wide = [_]u16{ 1, 1 };
                const a: Outer(u16, 2) = .{ .pair = .{ .k = 40, .v = 0 }, .items = &wide };
                const narrow = [_]u8{2};
                const b: Outer(u16, null) = .{ .pair = .{ .k = 0, .v = 0 }, .items = &narrow };
                return @as(u8, @intCast(a.pair.k)) + b.items[0];
            }
            """);
        // Declared AFTER the fields that use them, as in std: member order does not matter in zig.
        cs.ShouldMatch(@"struct Outer__u16_opt2\s*\{[^}]*Inner__u16_u8 pair;[^}]*ConstSlice<ushort> items;");
        cs.ShouldMatch(@"struct Outer__u16_optnull\s*\{[^}]*ConstSlice<byte> items;");
    }
}
