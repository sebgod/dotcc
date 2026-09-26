#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for GENERIC container methods (road-to-zig-std G4, lifting the W3/W5 free-functions-only cut):
/// a method with an <c>anytype</c> parameter (hash_map's <c>fetchRemoveAdapted(self, key: anytype, ctx:
/// anytype)</c>), with a <c>comptime</c> value typed by the owner's seed and spelled in its return type
/// (array_list's <c>toOwnedSliceSentinel(…, comptime sentinel: T) !SentinelSlice(sentinel)</c>), and a static
/// generic called through the type. Each instance is declared under its owner with the owner's comptime
/// seeds live and lowered inside it. End-to-end in the <c>generic_methods</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigGenericMethodTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-ziggm-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void An_anytype_method_instantiates_per_argument_type_with_the_receiver_first()
    {
        var cs = EmitZig("""
            const Ctx = struct {
                pub fn eql(_: Ctx, a: u16, b: u16) bool { return a == b; }
            };
            fn Store(comptime K: type) type {
                return struct {
                    k: K,
                    const Self = @This();
                    pub fn matches(self: *const Self, key: anytype, ctx: anytype) bool {
                        return ctx.eql(key, self.k);
                    }
                };
            }
            pub fn main() u8 {
                const s: Store(u16) = .{ .k = 42 };
                return if (s.matches(@as(u16, 42), Ctx{})) 42 else 0;
            }
            """);
        cs.ShouldMatch(@"CBool Store__u16_matches__u16_Ctx\(Store__u16\* self, ushort key, Ctx ctx\)");
        cs.ShouldContain("Store__u16_matches__u16_Ctx(&s, ");
    }

    [Fact]
    public void A_comptime_value_method_parameter_is_typed_by_the_owner_and_spells_its_return_type()
    {
        var cs = EmitZig("""
            fn Tagged(comptime T: type, comptime tag: T) type {
                return struct { v: T, pub fn tagOf(_: @This()) T { return tag; } };
            }
            fn Store(comptime K: type) type {
                return struct {
                    k: K,
                    const Self = @This();
                    pub fn with(self: Self, comptime tag: K) Tagged(K, tag) {
                        return .{ .v = self.k };
                    }
                };
            }
            pub fn main() u8 {
                const s: Store(u8) = .{ .k = 40 };
                const t = s.with(2);
                return t.v + t.tagOf();
            }
            """);
        cs.ShouldContain("Tagged__u8_2 Store__u8_with__2(Store__u8 self)");
        cs.ShouldContain("unsafe struct Tagged__u8_2");
    }

    [Fact]
    public void A_static_generic_method_is_called_through_its_type()
    {
        var cs = EmitZig("""
            fn Store(comptime K: type) type {
                return struct {
                    pub fn widen(comptime T: type, k: K) T { return @intCast(k); }
                };
            }
            pub fn main() u8 {
                const w = Store(u8).widen(u32, 42);
                return @intCast(w);
            }
            """);
        cs.ShouldContain("uint Store__u8_widen__u32(byte k)");
        // A generic method template has no body of its own.
        cs.ShouldNotMatch(@"\bStore__u8_widen\(");
    }
}
