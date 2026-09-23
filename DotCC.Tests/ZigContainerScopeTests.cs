#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for container-scoped declarations that std's hash_map and debug.zig lean on (road-to-zig-std
/// G3): a NESTED container inside a reified (type-returning generic) struct, a container's own TYPE const
/// typing a field (<c>const FingerPrint = u7;</c>), a field default that names a sibling const or chooses
/// with an <c>if</c>, an <c>if</c> choosing between two inline container types, a module-level comptime bool
/// folded through a <c>switch</c> over a <c>builtin</c> tag, and the rest of a block after a comptime-taken
/// <c>return</c> left unanalysed. End-to-end in the <c>reified_nested_containers</c> and
/// <c>comptime_type_arms</c> zig-oracle programs.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigContainerScopeTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigcsc-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_nested_container_of_a_reified_struct_is_flattened_per_instance_with_its_methods()
    {
        var cs = EmitZig("""
            fn Map(comptime K: type, comptime V: type) type {
                return struct {
                    head: Entry,
                    pub const Entry = struct {
                        key: K,
                        value: V,
                        pub fn sum(e: Entry) u8 {
                            return @as(u8, @intCast(e.key)) + e.value;
                        }
                    };
                };
            }
            pub fn main() u8 {
                const m: Map(u16, u8) = .{ .head = .{ .key = 40, .value = 2 } };
                return m.head.sum();
            }
            """);
        cs.ShouldContain("unsafe struct Map__u16_u8__Entry");
        cs.ShouldContain("public ushort key;");
        cs.ShouldContain("byte Map__u16_u8__Entry_sum(Map__u16_u8__Entry e)");
        cs.ShouldContain("public Map__u16_u8__Entry head;");
    }

    [Fact]
    public void A_container_type_const_types_a_field_and_a_sibling_const_is_its_default()
    {
        var cs = EmitZig("""
            const Metadata = packed struct {
                const FingerPrint = u7;
                const free: FingerPrint = 5;
                fingerprint: FingerPrint = free,
                used: u1 = 0,
            };
            pub fn main() u8 {
                const m: Metadata = .{};
                return m.fingerprint;
            }
            """);
        cs.ShouldContain("public byte fingerprint;");
        cs.ShouldMatch(@"new Metadata \{ fingerprint = \(?5");
    }

    [Fact]
    public void An_if_over_two_inline_enum_types_keeps_only_the_taken_arm()
    {
        var cs = EmitZig("""
            const checked = false;
            const Guard = struct {
                state: State = if (checked) .unlocked else .unknown,
                pub const State = if (checked) enum { unknown, unlocked, locked } else enum { only, unknown };
            };
            pub fn main() u8 {
                const g: Guard = .{};
                return @as(u8, @intFromEnum(g.state)) + 41;
            }
            """);
        cs.ShouldMatch(@"enum __AnonEnum\d+ : int\s*\{\s*only = 0,\s*unknown = 1,\s*\}");
        cs.ShouldNotMatch(@"\bunlocked\b");
        cs.ShouldMatch(@"state = __AnonEnum\d+\.unknown");
    }

    [Fact]
    public void A_module_level_bool_folds_through_a_switch_over_a_builtin_tag()
    {
        var cs = EmitZig("""
            const builtin = @import("builtin");
            const checked = switch (builtin.cpu.arch) {
                .avr, .msp430 => false,
                else => true,
            };
            const Guard = struct {
                pub const State = if (checked) enum { on } else enum { off };
                s: State,
            };
            pub fn main() u8 {
                const g: Guard = .{ .s = .on };
                _ = g;
                return 42;
            }
            """);
        cs.ShouldMatch(@"enum __AnonEnum\d+ : int\s*\{\s*on = 0,\s*\}");
        cs.ShouldNotMatch(@"enum __AnonEnum\d+ : int\s*\{\s*off");
    }

    [Fact]
    public void The_rest_of_a_block_after_a_comptime_taken_return_is_not_analysed()
    {
        // `.locked` exists only in the enum the other mode chooses; zig never analyses the line after
        // the folded `return`, and neither may dotcc.
        var cs = EmitZig("""
            const checked = false;
            const Guard = struct {
                state: State = .unknown,
                pub const State = if (checked) enum { unknown, locked } else enum { unknown };
                pub fn lock(g: *Guard) void {
                    if (!checked) return;
                    g.state = .locked;
                }
            };
            pub fn main() u8 {
                var g: Guard = .{};
                g.lock();
                return 42;
            }
            """);
        cs.ShouldNotMatch(@"\.locked\b|locked = ");
        cs.ShouldMatch(@"void Guard_lock\(Guard\* g\)\s*\{\s*return;\s*\}");
    }
}
