#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for the comptime engine segment (the maintainer's choice: extend the IR interpreter). E1: a pointer
/// to a comptime AGGREGATE is the aggregate itself, so a comptime call can hand a struct to a
/// <c>self: *@This()</c> method, or an array to a <c>*[N]T</c> parameter, and see the mutation. Also the runtime
/// fix that fell out: <c>buf[i]</c> through a <c>*[N]T</c> indexes the array's elements. E2: a comptime value
/// needed during lowering (an array extent, a folded optional capture) runs its callee, whose body lowers on
/// demand, once. E3: a <c>comptime var</c> of a struct type lives across statements, mutated by comptime
/// method calls. End-to-end in the <c>comptime_pointer_to_aggregate</c>, <c>comptime_values_during_lowering</c>
/// and <c>comptime_struct_var</c> zig-oracle programs.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigComptimeEngineTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigce-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_comptime_call_mutates_a_struct_and_an_array_through_pointers()
    {
        var cs = EmitZig("""
            const Acc = struct {
                n: u32,
                fn add(self: *Acc, v: u32) void { self.n += v; }
            };
            fn fill(buf: *[2]u32, v: u32) void { buf[1] = v; }
            fn total() u32 {
                var a = Acc{ .n = 0 };
                a.add(40);
                var buf = [2]u32{ 0, 0 };
                fill(&buf, 2);
                return a.n + buf[1];
            }
            pub fn main() u8 {
                const t = comptime total();
                return @intCast(t);
            }
            """);
        cs.ShouldContain("uint t = 42u;");
        // The runtime body indexes the pointed-at array's elements.
        cs.ShouldContain("buf[1] = v;");
    }

    [Fact]
    public void An_array_extent_calls_a_later_function_whose_body_lowers_on_demand_once()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                var i: u8 = 1;
                while (i < 2) : (i += 1) {
                    var buf: [three()]u8 = undefined;
                    buf[0] = i;
                    return buf[0] + @as(u8, buf.len);
                }
                return 0;
            }
            fn three() usize {
                var i: usize = 0;
                i += 3;
                return i;
            }
            """);
        cs.ShouldContain("byte* buf = stackalloc byte[3];");
        System.Text.RegularExpressions.Regex.Matches(cs, @"static unsafe ulong three\(\)").Count.ShouldBe(1);
    }

    [Fact]
    public void A_comptime_optional_call_folds_its_capture_both_ways()
    {
        var cs = EmitZig("""
            fn blockLen(comptime T: type) ?comptime_int {
                var n: comptime_int = 0;
                if (@sizeOf(T) > 4) return null;
                n = @sizeOf(T) * 8;
                return n;
            }
            pub fn main() u8 {
                var total: u8 = 1;
                if (comptime blockLen(u16)) |bl| {
                    total += bl;
                }
                if (comptime blockLen(u64)) |bl| {
                    total += bl;
                } else {
                    total += 2;
                }
                return total;
            }
            """);
        cs.ShouldContain("total += (byte)(16L);");
        cs.ShouldContain("total += (byte)(2);");
        cs.ShouldNotContain("__cap");
    }

    [Fact]
    public void A_comptime_struct_var_is_mutated_by_comptime_method_calls()
    {
        var cs = EmitZig("""
            const St = struct {
                next: usize = 0,
                used: u32 = 0,
                pub fn take(self: *@This(), want: ?usize) ?usize {
                    const i = want orelse init: {
                        const n = self.next;
                        self.next += 1;
                        break :init n;
                    };
                    if (i >= 3) return null;
                    self.used |= @as(u32, 1) << @as(u5, @intCast(i));
                    return i;
                }
                pub fn allUsed(self: *@This()) bool {
                    return @popCount(self.used) == 3;
                }
            };
            pub fn main() u8 {
                comptime var st: St = .{};
                const a = comptime st.take(null) orelse 9;
                const b = comptime st.take(2) orelse 9;
                const c = comptime st.take(null) orelse 9;
                const d = comptime st.take(null) orelse 9;
                var r: u8 = @intCast(a + b + c + d);
                if (comptime st.allUsed()) r += 1;
                return r;
            }
            """);
        cs.ShouldContain("ulong a = 0UL;");
        cs.ShouldContain("ulong b = 2UL;");
        cs.ShouldContain("ulong c = 1UL;");
        cs.ShouldContain("ulong d = 2UL;");
        cs.ShouldContain("if (Cond.B(true))");
        cs.ShouldNotMatch(@"\dUL \?\? ");
    }

    [Fact]
    public void A_runtime_orelse_labeled_block_runs_only_on_null()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                var total: u8 = 1;
                var maybe: ?u8 = null;
                if (total == 1) maybe = 41;
                const g = maybe orelse fb: {
                    total += 100;
                    break :fb 0;
                };
                return total + g;
            }
            """);
        cs.ShouldMatch(@"if \(Cond\.B\(\(Cond\.B\(maybe\.HasValue\) \? 0 : 1\)\)\)\s*\{\s*byte __blk\d+ = default\(byte\);");
        cs.ShouldContain("= maybe.Value;");
    }

    [Fact]
    public void A_switch_over_a_comptime_union_selects_its_prong_with_the_payload_bound()
    {
        var cs = EmitZig("""
            const Spec = union(enum) { none, number: usize };
            const Ph = struct { arg: Spec, spec: []const u8 = "" };
            fn parse(comptime s: []const u8) Ph {
                if (s.len == 0) return .{ .arg = .{ .none = {} } };
                return .{ .arg = .{ .number = s.len }, .spec = s[1..] };
            }
            pub fn main() u8 {
                const p = comptime parse("abc");
                const pos = comptime switch (p.arg) {
                    .none => 0,
                    .number => |n| n,
                };
                return @intCast(pos + p.spec.len + 37);
            }
            """);
        // The prong is chosen at lowering time: no runtime union switch, the payload is the literal 3.
        cs.ShouldNotContain("__un");
        cs.ShouldMatch(@"__vcf\d+ = 3UL;");
        // The comptime byte slice `s[1..]` ("bc", 2 bytes: the literal's NUL is not counted) splices as a string.
        cs.ShouldContain(@"Libc.L(""\x62\x63\0""u8), 2UL)");
    }

    [Fact]
    public void An_open_slice_of_a_string_literal_excludes_its_nul()
    {
        var cs = EmitZig("""
            fn tail(comptime s: []const u8) []const u8 {
                return s[1..];
            }
            pub fn main() u8 {
                return @intCast(tail("abc").len);
            }
            """);
        cs.ShouldContain("3UL - (ulong)1");   // "abc" is 3 bytes long in zig, the NUL uncounted
    }
}
