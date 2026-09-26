#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for what std.mem.sort from real std needed (road-to-zig-std, task #48): a struct declared INSIDE a function
/// with methods (std.sort's local <c>Context</c>) whose bodies read the enclosing instance's comptime seeds, a comparator
/// named through its container (<c>ByMod.less</c>), a <c>void</c> field (a <c>{}</c> context) with no storage,
/// <c>@ptrCast</c> of a single-item pointer to a byte slice (std.mem.swap), a folded comptime_int too wide for
/// <c>int</c> (std.math.sqrt_int's <c>maxInt(T)</c>), and arrays copied by VALUE (<c>var b = a;</c> used to alias the
/// storage, a silent miscompile). End-to-end in the <c>local_struct_methods</c> and <c>array_value_copies</c>
/// zig-oracle programs and the real-std <c>std.mem.sort</c> differential.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigSortTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigsort-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_local_struct_s_methods_see_the_enclosing_instance_s_comptime_seeds()
    {
        var cs = EmitZig("""
            fn sortWith(comptime T: type, items: []T, context: anytype, comptime lessThanFn: fn (@TypeOf(context), T, T) bool) void {
                const Context = struct {
                    items: []T,
                    sub_ctx: @TypeOf(context),
                    pub fn lessThan(ctx: @This(), a: usize, b: usize) bool {
                        return lessThanFn(ctx.sub_ctx, ctx.items[a], ctx.items[b]);
                    }
                };
                const ctx = Context{ .items = items, .sub_ctx = context };
                if (ctx.lessThan(1, 0)) {
                    const t = items[0];
                    items[0] = items[1];
                    items[1] = t;
                }
            }
            fn lt(_: void, a: u8, b: u8) bool {
                return a < b;
            }
            const Rev = struct {
                fn gt(_: Rev, a: u8, b: u8) bool {
                    return a > b;
                }
            };
            pub fn main() u8 {
                var xs = [_]u8{ 5, 3 };
                sortWith(u8, &xs, {}, lt);
                sortWith(u8, &xs, Rev{}, Rev.gt);
                return xs[0];
            }
            """);
        // One local struct per instance, its method calling the instance's comptime comparator.
        cs.ShouldMatch(@"CBool sortWith__u8_void_lt__Context_lessThan\(sortWith__u8_void_lt__Context ctx, ulong a, ulong b\)\s*\{\s*return lt\(");
        cs.ShouldContain("sortWith__u8_Rev_Rev_gt__Context_lessThan(");
        // The `void` context field has no storage: not in the layout, not in the initializer.
        cs.ShouldContain("new sortWith__u8_void_lt__Context { items = items }");
        cs.ShouldNotContain("public void sub_ctx;");
    }

    [Fact]
    public void An_array_read_as_a_value_is_copied_not_aliased()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                var a = [_]u8{ 1, 2, 3 };
                var b = a;
                const c: [3]u8 = a;
                var d: [3]u8 = undefined;
                d = a;
                a[0] = 9;
                b[1] = 8;
                return a[0] + b[0] + b[1] + c[0] + d[0] + d[1];
            }
            """);
        cs.ShouldContain("byte* b = stackalloc byte[3];");
        cs.ShouldContain("ZigMem.CopyForwards<byte>(new Slice<byte>(b, 3UL), new ConstSlice<byte>(a, 3UL));");
        cs.ShouldContain("ZigMem.CopyForwards<byte>(new Slice<byte>(c, 3UL), new ConstSlice<byte>(a, 3UL));");
        cs.ShouldContain("ZigMem.CopyForwards<byte>(new Slice<byte>(d, 3UL), new ConstSlice<byte>(a, 3UL));");
        cs.ShouldNotContain("byte* b = a;");
    }

    [Fact]
    public void A_single_item_pointer_casts_to_a_byte_slice_and_a_wide_comptime_int_keeps_its_value()
    {
        var cs = EmitZig("""
            fn big(comptime T: type) comptime_int {
                return (1 << @bitSizeOf(T)) - 1;
            }
            fn bytes(comptime T: type, p: *T) []u8 {
                return @ptrCast(p);
            }
            pub fn main() u8 {
                const m = big(u64);
                var x: u32 = 0x01020304;
                const b = bytes(u32, &x);
                return @intCast((m % 7) + b.len + b[0]);
            }
            """);
        cs.ShouldContain("ulong m = 18446744073709551615UL;");
        cs.ShouldContain("return new Slice<byte>((byte*)p, 4UL);");
    }
}
