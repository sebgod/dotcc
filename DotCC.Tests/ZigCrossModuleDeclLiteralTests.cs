#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for the array_list surface (road-to-zig-std G4): a decl literal VALUE of a container another
/// module declares (<c>var list: list.List(u8) = .empty;</c>) lowered by that module; a container const
/// evaluated in its container's scope (<c>pub const empty: Self = …</c>); the empty slice spelled
/// <c>&amp;.{}</c> or <c>&amp;[_]T{}</c>; a bare call to a sibling container function (<c>self.* = init(…)</c>);
/// and <c>@memmove</c>. End-to-end in the <c>cross_module_decl_literal</c> multi-file zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigCrossModuleDeclLiteralTests
{
    private static string EmitZigPair(string main, string siblingName, string sibling)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zigdl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, siblingName), sibling);
            var mainPath = Path.Combine(dir, "main.zig");
            File.WriteAllText(mainPath, main);
            return Compiler.EmitCSharp(new[] { mainPath });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private const string ListZig = """
        pub fn List(comptime T: type) type {
            return struct {
                items: []T,
                used: usize,
                const Self = @This();
                pub const empty: Self = .{ .items = &.{}, .used = 0 };
                pub fn fromEmpty() Self {
                    return .{ .items = &[_]T{}, .used = 0 };
                }
                pub fn reset(self: *Self) void {
                    self.* = fromEmpty();
                }
                pub fn shiftLeft(buf: []T) void {
                    @memmove(buf[0 .. buf.len - 1], buf[1..]);
                }
            };
        }
        """;

    [Fact]
    public void A_decl_literal_value_of_another_modules_container_lowers_in_that_module()
    {
        var cs = EmitZigPair("""
            const list = @import("list.zig");
            pub fn main() u8 {
                var a: list.List(u8) = .empty;
                a.used = 42;
                return @intCast(a.used + a.items.len);
            }
            """, "list.zig", ListZig);
        // `.empty` is the owner's `.{ .items = &.{}, .used = 0 }`: an empty slice, a null pointer of length 0.
        cs.ShouldContain("list__List__u8 a = new list__List__u8 { items = new Slice<byte>(null, 0UL), used = 0 };");
    }

    [Fact]
    public void A_bare_sibling_call_and_memmove_lower_inside_the_container()
    {
        var cs = EmitZigPair("""
            const list = @import("list.zig");
            pub fn main() u8 {
                var a: list.List(u8) = .empty;
                a.reset();
                var buf = [_]u8{ 1, 2, 3 };
                list.List(u8).shiftLeft(&buf);
                return buf[0] + 40;
            }
            """, "list.zig", ListZig);
        cs.ShouldMatch(@"\*self = list__List__u8_fromEmpty\(\);");
        cs.ShouldContain("ZigMem.Move<byte>(");
    }

    [Fact]
    public void A_module_qualified_type_is_callable_and_an_inline_import_member_is_a_type_alias()
    {
        // `h.H.hash(…)` (a static call through a module-qualified type) and `const H2 = @import("h.zig").H;`
        // (a type alias rooted at an inline import), both user-level spellings of what std reaches by aliases.
        var cs = EmitZigPair("""
            const h = @import("h.zig");
            const H2 = @import("h.zig").H;
            pub fn main() u8 {
                const a = h.H.hash(0, 0);
                const b: H2 = H2.init(1);
                return @intCast(a + b.a - 41);
            }
            """, "h.zig", """
            pub const H = struct {
                a: u64,
                pub fn init(seed: u64) H { return .{ .a = seed ^ 40 }; }
                pub fn hash(seed: u64, n: u64) u64 { return H.init(seed).a + n + 2; }
            };
            """);
        cs.ShouldContain("h__H_hash(0, 0)");
        cs.ShouldContain("h__H b = h__H_init(1);");
    }

    [Fact]
    public void A_module_qualified_nested_type_and_an_enum_nested_container_resolve()
    {
        // std.Target's shape: `tgt.Cpu.Arch` and `tgt.Cpu.Arch.Family` (the module prefix, then the owner's
        // nested containers), where `Family` is nested in an ENUM body.
        var cs = EmitZigPair("""
            const tgt = @import("tgt.zig");
            pub fn main() u8 {
                const a: tgt.Cpu.Arch = .aarch64;
                const f: tgt.Cpu.Arch.Family = a.family();
                return if (f == .arm) 42 else 0;
            }
            """, "tgt.zig", """
            pub const Cpu = struct {
                arch: Arch,
                pub const Arch = enum {
                    x86_64,
                    aarch64,
                    pub const Family = enum { x86, arm };
                    pub fn family(arch: Arch) Family {
                        return switch (arch) {
                            .x86_64 => .x86,
                            .aarch64 => .arm,
                        };
                    }
                };
            };
            """);
        cs.ShouldContain("tgt__Cpu__Arch a = tgt__Cpu__Arch.aarch64;");
        cs.ShouldContain("tgt__Cpu__Arch__Family f = tgt__Cpu__Arch_family(a);");
    }
}
