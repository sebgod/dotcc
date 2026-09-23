#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for a TYPE-returning container member (road-to-zig-std G4: hash_map's
/// <c>fn FieldIterator(comptime T: type) type</c>, array_list's <c>SentinelSlice</c>): a comptime type
/// constructor declared under its container, reached bare from inside the container, through a type const
/// (<c>pub const KeyIterator = FieldIterator(K);</c>) or through the container type (<c>Self.FieldIterator(K)</c>),
/// and evaluated in its owner's scope with the owner's comptime seeds live, so the struct it returns sees
/// the owner's nested types. End-to-end in the <c>type_returning_methods</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigTypeReturningMethodTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigtrm-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_type_returning_method_of_a_reified_struct_reifies_per_owner_instance_and_argument()
    {
        var cs = EmitZig("""
            fn Table(comptime K: type, comptime V: type) type {
                return struct {
                    key: K,
                    value: V,
                    const Self = @This();
                    const Mark = struct { used: bool };
                    pub const ValueBox = Box(V);
                    fn Box(comptime T: type) type {
                        return struct {
                            item: T,
                            mark: Mark,
                            pub fn get(self: @This()) T {
                                return if (self.mark.used) self.item else 0;
                            }
                        };
                    }
                    pub fn valueBox(self: Self) ValueBox {
                        return .{ .item = self.value, .mark = .{ .used = true } };
                    }
                    pub fn keyBox(self: Self) Self.Box(K) {
                        return .{ .item = self.key, .mark = .{ .used = true } };
                    }
                };
            }
            pub fn main() u8 {
                const t: Table(u16, u8) = .{ .key = 40, .value = 2 };
                return @as(u8, @intCast(t.keyBox().get())) + t.valueBox().get();
            }
            """);
        cs.ShouldContain("unsafe struct Table__u16_u8_Box__u8");
        cs.ShouldContain("unsafe struct Table__u16_u8_Box__u16");
        // The made struct sees the owner's nested type, and its method is an ordinary reified method.
        cs.ShouldContain("public Table__u16_u8__Mark mark;");
        cs.ShouldContain("ushort Table__u16_u8_Box__u16_get(Table__u16_u8_Box__u16 self)");
        // A comptime type constructor emits no runtime function of its own.
        cs.ShouldNotMatch(@"\bTable__u16_u8_Box\(");
    }

    [Fact]
    public void A_type_returning_method_of_a_plain_struct_is_a_namespaced_type_constructor()
    {
        var cs = EmitZig("""
            const Shapes = struct {
                fn Pair(comptime T: type) type {
                    return struct { a: T, b: T };
                }
                pub const Bytes = Pair(u8);
            };
            pub fn main() u8 {
                const p: Shapes.Bytes = .{ .a = 40, .b = 2 };
                const q: Shapes.Pair(u16) = .{ .a = 1, .b = 1 };
                return p.a + p.b + @as(u8, @intCast(q.a - q.b));
            }
            """);
        cs.ShouldContain("unsafe struct Shapes_Pair__u8");
        cs.ShouldContain("unsafe struct Shapes_Pair__u16");
    }
}
