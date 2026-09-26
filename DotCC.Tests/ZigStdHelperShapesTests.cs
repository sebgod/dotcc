#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for the shapes std's small helpers needed (road-to-zig-std, tasks #57 to #59): <c>@TypeOf(a, b, c)</c> as the
/// operands' peer type (std.math.clamp), an empty array literal and <c>allocator.dupe</c> in a value <c>if</c> at a
/// <c>![]u8</c> return (std.mem.join), a local <c>const M = switch (T) { f16, f32, f64 => u64, … }</c> and
/// <c>T == f16 or T == f32</c> over zig floats dotcc does not lower, a value <c>if</c> / <c>switch</c> as a named
/// field's value, <c>&amp;.{ i.base + 'a' }</c> as a comptime string, <c>inline for</c> over a comptime string, and a
/// value <c>if (c) return x else y</c> (std.fmt.parse_float). End to end in the <c>std_helper_shapes</c> zig-oracle
/// program and the real-std helpers differential.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigStdHelperShapesTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zighelp-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private const string Program = """
        const std = @import("std");

        fn clampLike(val: anytype, lower: anytype, upper: anytype) @TypeOf(val, lower, upper) {
            return @max(lower, @min(val, upper));
        }

        fn mantissaSize(comptime T: type) usize {
            const M = switch (T) {
                f16, f32, f64 => u64,
                f80, f128 => u128,
                else => unreachable,
            };
            if (T == f16 or T == f32 or T == f64) {
                return @sizeOf(M);
            }
            return 1;
        }

        const Info = struct { base: u8, max: u8 };

        fn info(comptime T: type) Info {
            return .{ .base = switch (T) {
                u64 => 10,
                else => 16,
            }, .max = if (T == u64) 19 else 38 };
        }

        fn hasAny(s: []const u8, comptime cs: []const u8) bool {
            for (s) |c| {
                inline for (cs) |d| if (c == d) return true;
            }
            return false;
        }

        fn hasSep(s: []const u8, comptime i: Info) bool {
            return hasAny(s, &.{i.base + 'a'});
        }

        fn firstOrNull(s: []const u8) ?u8 {
            return if (s.len > 0) return s[0] else null;
        }

        fn dupeOrEmpty(a: std.mem.Allocator, z: bool) ![]u8 {
            return if (z) try a.dupe(u8, &[1]u8{7}) else &[0]u8{};
        }

        pub fn main() !u8 {
            const a = std.heap.page_allocator;
            const d = try dupeOrEmpty(a, true);
            defer a.free(d);
            const e = try dupeOrEmpty(a, false);
            const none = [_]u8{};
            const i = comptime info(u64);
            const c: u16 = clampLike(@as(u16, 300), 1, 250);
            var total: usize = c - 200;
            total += mantissaSize(f64) + i.base + i.max + d[0] + e.len + none.len;
            total += @intFromBool(hasSep("xk", i)) + @intFromBool(hasSep("xy", i));
            total += (firstOrNull("A") orelse 0) - 'A' + 1;
            return @intCast(total);
        }
        """;

    [Fact]
    public void TypeOf_over_several_operands_is_their_peer_type()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("internal static unsafe ushort clampLike__u16_ci1_ci250(ushort val)");
    }

    [Fact]
    public void A_type_switch_and_type_comparisons_pass_over_floats_dotcc_does_not_lower()
    {
        var cs = EmitZig(Program);
        cs.ShouldMatch(@"ulong mantissaSize__f64\(\)\s*\{\s*\{\s*return \(\(ulong\)\(sizeof\(ulong\)\)\);");
    }

    [Fact]
    public void A_named_field_value_may_be_a_value_switch_or_if()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("return new Info { @base = 10, max = 19 };");
    }

    [Fact]
    public void An_anonymous_list_of_comptime_bytes_is_a_comptime_string_an_inline_for_unrolls()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("byte d = 107;");   // `i.base + 'a'` = 10 + 97
    }

    [Fact]
    public void A_value_if_whose_then_arm_returns_hoists_the_return()
    {
        var cs = EmitZig(Program);
        cs.ShouldMatch(@"byte\? firstOrNull\(ConstSlice<byte> s\)\s*\{\s*if \(Cond\.B\(\(\(CBool\)\(s\.Len > \(ulong\)\(0\)\)\)\)\)\s*return s\.Ptr\[0\];\s*return null;");
    }

    [Fact]
    public void Dupe_and_an_empty_array_at_an_error_union_slice_return()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("byte* __cl0 = stackalloc byte[]{ 7 };");
        cs.ShouldContain("return ErrUnion<Slice<byte>>.Ok((Cond.B(z) ? ErrUnion.Try(ZigAlloc.Dupe(a, new ConstSlice<byte>(__cl0, 1UL), 1)) : new Slice<byte>(null, 0UL)));");
    }

    [Fact]
    public void An_empty_inferred_array_literal_is_a_zero_length_array()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("stackalloc byte[]{  };");
    }

    private const string CompoundProgram = """
        fn firstOrNull(s: []const u8) ?u8 {
            if (s.len == 0) return null;
            return s[0];
        }

        pub fn main() u8 {
            var total: u8 = 1;
            total += if (firstOrNull("")) |_| 100 else 2;
            total *= if (firstOrNull("B")) |_| 2 else 3;
            return total;
        }
        """;

    [Fact]
    public void A_compound_assignment_hoists_a_captured_value_if()
    {
        var cs = EmitZig(CompoundProgram);
        cs.ShouldContain("total += __ifcap1;");
        cs.ShouldContain("total *= __ifcap3;");
    }

    [Fact]
    public void A_compound_assignment_with_a_side_effecting_target_does_not_reorder_its_value()
    {
        Should.Throw<Exception>(() => EmitZig("""
            fn firstOrNull(s: []const u8) ?u8 {
                if (s.len == 0) return null;
                return s[0];
            }
            var calls: u8 = 0;
            fn bump() u8 {
                calls += 1;
                return calls;
            }
            pub fn main() u8 {
                var buf = [_]u8{ 0, 0 };
                buf[bump() - 1] += if (firstOrNull("x")) |c| c else 0;
                return buf[0];
            }
            """)).Message.ShouldContain("can't be hoisted past an earlier side-effecting operand");
    }

    private const string BiasedProgram = """
        fn assert(ok: bool) void {
            if (!ok) unreachable;
        }

        fn mantissaType(comptime T: type) type {
            return switch (T) {
                f16, f32, f64 => u64,
                f80, f128 => u128,
                else => unreachable,
            };
        }

        fn Biased(comptime T: type) type {
            const MantissaT = mantissaType(T);
            return struct {
                const Self = @This();
                f: MantissaT,
                e: i32,

                pub fn word(self: Self) MantissaT {
                    var w: MantissaT = self.f;
                    w |= @as(MantissaT, @intCast(self.e)) << 8;
                    return w;
                }

                pub fn scaled(self: Self, comptime k: u8) MantissaT {
                    return self.f * @as(MantissaT, k);
                }
            };
        }

        fn check(comptime T: type) bool {
            assert(T == f16 or T == f32 or T == f64);
            return @sizeOf(T) == 8;
        }

        pub fn main() u8 {
            const b = Biased(f64){ .f = 3, .e = 1 };
            const w = b.word();
            return @intCast(w % 256 + (w >> 8) + b.scaled(4) + @intFromBool(check(f64)));
        }
        """;

    [Fact]
    public void A_type_bodys_own_type_alias_reaches_its_methods_and_generic_method_instances()
    {
        var cs = EmitZig(BiasedProgram);
        cs.ShouldContain("w |= (ulong)(ulong)self.e << 8;");                  // `@as(MantissaT, …)` in a deferred method
        cs.ShouldMatch(@"ulong Biased__f64_scaled__4\(Biased__f64 self\)\s*\{\s*return self\.f \* \(ulong\)4;");   // a generic method
    }

    [Fact]
    public void A_type_comparison_folds_as_a_call_argument()
    {
        var cs = EmitZig(BiasedProgram);
        cs.ShouldContain("assert(((CBool)(Cond.B(((CBool)(Cond.B(false) || Cond.B(false)))) || Cond.B(true))));");
    }

    [Fact]
    public void A_switch_prong_body_may_be_a_compound_assignment()
    {
        var cs = EmitZig("""
            var hits: u32 = 0;
            pub fn main() u8 {
                var bits: u8 = 0b0001;
                for ([_]u8{ 0, 1, 2, 3, 1, 0 }) |x| {
                    switch (x) {
                        0 => hits += 1,
                        1 => hits *= 3,
                        2 => bits |= 0b1000,
                        else => hits -%= 1,
                    }
                }
                var v: u8 = 7;
                switch (bits) {
                    9 => v <<= 2,
                    else => v = 0,
                }
                return @intCast(hits + bits + v);
            }
            """);
        cs.ShouldContain("hits += (uint)(1);");
        cs.ShouldContain("hits *= (uint)(3);");
        cs.ShouldContain("bits |= (byte)(8);");
        cs.ShouldContain("v <<= 2;");
    }

    [Fact]
    public void Debug_print_s_of_a_byte_slice_passes_the_slice_itself()
    {
        var cs = EmitZig("""
            const std = @import("std");
            pub fn main() void {
                const word: []const u8 = "hello world";
                var buf = [_]u8{ 'a', 'b', 'c', 'd' };
                const mut: []u8 = buf[1..3];
                std.debug.print("[{s}] [{s}] [{s}]\n", .{ word[0..5], mut, word[6..] });
            }
            """);
        // The runtime builder's slice overload prints exactly `.len` bytes; no NUL is read.
        cs.ShouldContain(".Arg(new ConstSlice<byte>(word.Ptr + 0, unchecked((ulong)(5 - 0)))).Arg(mut)");
    }

    [Fact]
    public void Float_builtins_route_to_System_Math_with_zigs_rounding()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                @setRuntimeSafety(false);
                var x: f64 = 2.5;
                var y: f32 = 6.25;
                _ = .{ &x, &y };
                const r = @round(x) + @sqrt(x * 10.0) + @abs(-x);
                const q = @sqrt(y);
                const f: u8 = @intFromFloat(r + q);
                return f;
            }
            """);
        // @round rounds half away from zero (C# Math.Round would round 2.5 to even); f32 keeps MathF.
        cs.ShouldContain("double r = ZigMath.RoundAway(x) + System.Math.Sqrt(x * 10.0) + System.Math.Abs(-x);");
        cs.ShouldContain("float q = System.MathF.Sqrt(y);");
        cs.ShouldNotContain("setRuntimeSafety");
    }

    [Fact]
    public void A_folded_if_returning_through_a_hoisted_catch_makes_the_rest_dead()
    {
        var cs = EmitZig("""
            fn powi(comptime T: type, x: T, y: T) error{Overflow}!T {
                if (y > 30) return error.Overflow;
                var acc: T = if (@typeInfo(T).int.bits < 1) unreachable else 1;
                var i: T = 0;
                while (i < y) : (i += 1) acc *= x;
                return acc;
            }
            fn pow(comptime T: type, x: T, y: T) T {
                if (@typeInfo(T) == .int) {
                    return powi(T, x, y) catch unreachable;
                }
                if (T != f32 and T != f64) {
                    @compileError("pow not implemented");
                }
                return x;
            }
            pub fn main() u8 {
                return @intCast(pow(u32, 3, 4));
            }
            """);
        // zig never analyses the `@compileError` after the taken prong's return (task #66), and the
        // `unreachable` arm leaves the ternary at the sink's type so the literal arm is cast.
        cs.ShouldContain("? throw new System.Diagnostics.UnreachableException(\"unreachable() reached\") : (uint)(1))");
    }

    [Fact]
    public void A_struct_layout_folds_and_a_packed_struct_compares_by_its_bytes()
    {
        var cs = EmitZig("""
            const Flags = packed struct { lo: u4, hi: u4 };
            const Ext = extern struct { p: u32 };
            const Plain = struct { p: u32 };
            fn kind(comptime T: type) u8 {
                return switch (@typeInfo(T).@"struct".layout) {
                    .auto => 1,
                    .@"extern" => 2,
                    .@"packed" => 3,
                };
            }
            pub fn main() u8 {
                const a = Flags{ .lo = 1, .hi = 2 };
                const b = Flags{ .lo = 1, .hi = 2 };
                return kind(Plain) * 100 + kind(Ext) * 10 + kind(Flags) + @intFromBool(a == b);
            }
            """);
        // Each instance returns its folded prong (task #70); zig's packed `==` compares the backing storage.
        cs.ShouldContain("public static bool operator ==(Flags a, Flags b) => System.MemoryExtensions.SequenceEqual(");
        cs.ShouldNotContain("operator ==(Ext a");
        cs.ShouldContain("(a == b)");
    }

    [Fact]
    public void A_while_true_with_a_continue_expression_has_no_condition()
    {
        var cs = EmitZig("""
            fn lastZero(s: []const u8) ?usize {
                var i: usize = s.len;
                while (true) : (i -= 1) {
                    if (i == 0) return null;
                    if (s[i - 1] == 0) return i - 1;
                }
            }
            pub fn main() u8 {
                return @intCast(lastZero(&[_]u8{ 1, 0, 2 }) orelse 9);
            }
            """);
        // An empty condition lets C# see the loop never falls out (Cond.B(true) is CS0161 here).
        cs.ShouldContain("for (; ; i -= (ulong)(1))");
    }

    [Fact]
    public void A_const_computed_by_a_labeled_block_is_evaluated_at_compile_time()
    {
        var cs = EmitZig("""
            const table = blk: {
                var t: [4]u32 = undefined;
                for (&t, 0..) |*e, i| {
                    e.* = @as(u32, @intCast(i)) * 5;
                }
                break :blk t;
            };
            const S = struct {
                const inner = blk: {
                    var t: [4]u32 = undefined;
                    for (&t, 0..) |*e, i| {
                        e.* = @as(u32, @intCast(i)) + 1;
                    }
                    break :blk t;
                };
                fn get(i: usize) u32 {
                    return inner[i];
                }
            };
            pub fn main() u8 {
                var i: usize = 3;
                _ = &i;
                return @truncate(table[i] + S.get(i));
            }
            """);
        // zig runs the block at compile time (task #79): the static holds the values, and no loop is emitted for it.
        cs.ShouldContain("uint* table = Libc.GlobalArrayFrom<uint>(new uint[]{ 0u, 5u, 10u, 15u });");
        cs.ShouldContain("uint* S__inner__static = Libc.GlobalArrayFrom<uint>(new uint[]{ 1u, 2u, 3u, 4u });");
    }

    [Fact]
    public void A_type_returning_generic_takes_a_comptime_struct_value()
    {
        var cs = EmitZig("""
            fn Algo(comptime W: type) type {
                return struct { poly: W, initial: W, refl: bool };
            }
            fn Crc(comptime W: type, comptime a: Algo(W)) type {
                return struct {
                    const Self = @This();
                    const table = blk: {
                        var t: [4]W = undefined;
                        for (&t, 0..) |*e, i| {
                            e.* = @as(W, @intCast(i)) * a.poly;
                        }
                        break :blk t;
                    };
                    crc: W,
                    pub fn init() Self {
                        const v = if (a.refl) a.initial + 1 else a.initial;
                        return Self{ .crc = v };
                    }
                    pub fn hash(b: []const u8) W {
                        var c = init();
                        for (b) |x| c.crc = table[x & 3] ^ (c.crc >> 1);
                        return c.crc;
                    }
                };
            }
            const C = Crc(u32, .{ .poly = 5, .initial = 7, .refl = true });
            pub fn main() u8 {
                return @truncate(C.hash("hello"));
            }
            """);
        // The struct argument keys the instance by its contents (task #71), and the members read its fields.
        cs.ShouldContain("__table__static = Libc.GlobalArrayFrom<uint>(new uint[]{ 0u, 5u, 10u, 15u });");
        cs.ShouldContain("return (byte)Crc__u32_c");
        cs.ShouldContain("_hash(new ConstSlice<byte>(");
    }

    [Fact]
    public void Bit_reverse_reverses_the_declared_width()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                var x: u8 = 0b0000_0110;
                var y: u3 = 0b011;
                _ = .{ &x, &y };
                return @bitReverse(x) + @as(u8, @bitReverse(y));
            }
            """);
        // A `u3` in its byte carrier reverses three bits, not eight.
        cs.ShouldContain("ZigMath.BitReverse(x, 8)");
        cs.ShouldContain("ZigMath.BitReverse(y, 3)");
    }

    [Fact]
    public void A_comptime_length_slice_dereference_passes_to_an_array_parameter()
    {
        var cs = EmitZig("""
            fn two(b: [2]u8) u16 {
                return @as(u16, b[0]) * 256 + b[1];
            }
            fn pair(s: []const u8) u16 {
                return two(s[0..2].*) + two(s[1..3].*);
            }
            pub fn main() u8 {
                return @truncate(pair("\x01\x02\x03"));
            }
            """);
        // std.unicode.utf8Decode's `utf8Decode2(bytes[0..2].*)` (task #72): the array parameter is its element pointer.
        cs.ShouldContain("two(new ConstSlice<byte>(s.Ptr + 0, unchecked((ulong)(2 - 0))).Ptr)");
    }

    [Fact]
    public void A_returned_if_or_switch_with_an_error_arm_is_the_error_union_itself()
    {
        var cs = EmitZig("""
            fn pick(x: u8) !u8 {
                return if (x < 10) x * 2 else error.TooBig;
            }
            fn kind(x: u8) !u8 {
                return switch (x) {
                    0 => 7,
                    1...5 => x + 1,
                    else => error.Bad,
                };
            }
            pub fn main() u8 {
                const a = pick(30) catch 100;
                const b = kind(9) catch 50;
                return a + b;
            }
            """);
        // Each arm is Ok or Err (task #72); wrapping the whole expression in Ok had returned the error code as a payload.
        cs.ShouldContain("? ErrUnion<byte>.Ok((byte)(x * 2)) : ErrUnion<byte>.Err(");
        cs.ShouldContain("_ => ErrUnion<byte>.Err(");
        cs.ShouldNotContain("ErrUnion<byte>.Ok((byte)((Cond.B(");
    }

    [Fact]
    public void Debug_print_braces_of_a_bool_prints_true_or_false()
    {
        var cs = EmitZig("""
            const std = @import("std");
            pub fn main() void {
                var x: u8 = 3;
                _ = &x;
                const t = x > 2;
                std.debug.print("{} {any}\n", .{ t, x < 1 });
            }
            """);
        // A local bound to a comparison is a zig `bool` (task #81), and `{}` prints it as a word.
        cs.ShouldContain("CBool t = ");
        cs.ShouldContain(""".Arg((Cond.B(t) ? Libc.L("true\0"u8) : Libc.L("false\0"u8)))""");
    }

    [Fact]
    public void A_signedness_tag_bound_to_a_const_builds_an_int_type()
    {
        var cs = EmitZig("""
            fn widen(comptime T: type, v: T) u16 {
                const signedness = @typeInfo(T).int.signedness;
                const W = @Int(signedness, 16);
                const w: W = v;
                return @bitCast(w);
            }
            pub fn main() u8 {
                return @truncate(widen(i8, -3) +% widen(u8, 200));
            }
            """);
        // std.mem.readVarInt's `const signedness = @typeInfo(ReturnType).int.signedness;` (task #76): a comptime tag.
        cs.ShouldContain("short w = v;");
        cs.ShouldContain("ushort w = v;");
    }

    [Fact]
    public void A_type_chosen_by_a_comptime_if_compares_equal_to_its_arm()
    {
        var cs = EmitZig("""
            const small = [_]u8{ 1, 2, 3 };
            const big = [_]u8{ 7, 8, 9, 10 };
            fn pick(comptime T: type) u8 {
                const DT = if (@bitSizeOf(T) <= 64) u64 else u128;
                const k: u8 = switch (DT) {
                    u64 => 1,
                    u128 => 2,
                    else => 3,
                };
                const tables = switch (DT) {
                    u64 => &small,
                    u128 => &big,
                    else => unreachable,
                };
                return @sizeOf(DT) + k + tables[1] + @as(u8, @intCast(tables.len));
            }
            pub fn main() u8 {
                return pick(f64) + pick(u128);
            }
            """);
        // std.fmt.float.render's `DT` (task #77): `DT == u64` holds (it had silently taken `else`), `&small` of an array
        // global is its storage pointer, and `.len` reads through a pointer to an array.
        cs.ShouldContain("byte k = 1;");
        cs.ShouldContain("byte* tables = (byte*)small;");
        cs.ShouldContain("unchecked((byte)(byte)3UL)");
    }

    [Fact]
    public void Array_list_index_and_capacity_members_map_onto_the_runtime_list()
    {
        var cs = EmitZig("""
            const std = @import("std");
            pub fn main() !u8 {
                const a = std.heap.page_allocator;
                var l: std.ArrayList(u16) = .empty;
                defer l.deinit(a);
                try l.appendSlice(a, &.{ 10, 20, 30 });
                try l.insert(a, 0, 5);
                const o = l.orderedRemove(1);
                const s = l.swapRemove(0);
                try l.ensureUnusedCapacity(a, 1);
                l.appendAssumeCapacity(60);
                l.shrinkRetainingCapacity(2);
                const last = l.getLast() orelse 0;
                return @intCast(o + s + last);
            }
            """);
        // Task #74: each member is one runtime ZigList call; getLast is `?T` in zig 0.17-dev.
        cs.ShouldContain(".Insert(ZigAlloc.CHeap(), 0, 5, ");
        cs.ShouldContain(".OrderedRemove(1)");
        cs.ShouldContain(".SwapRemove(0)");
        cs.ShouldContain(".EnsureUnusedCapacity(ZigAlloc.CHeap(), 1, ");
        cs.ShouldContain(".AppendAssumeCapacity(60)");
        cs.ShouldContain(".ShrinkRetainingCapacity(2)");
        cs.ShouldContain(".GetLast() ?? 0");
    }

    [Fact]
    public void A_global_struct_with_an_array_field_is_built_by_an_init_function()
    {
        var cs = EmitZig("""
            const alphabet = "ABCD".*;
            const Codec = struct { chars: [4]u8, table: [8]u8, pad: u8 };
            const codec = Codec{ .chars = alphabet, .table = @splat(0xff), .pad = '=' };
            pub fn main() u8 {
                return codec.chars[@truncate(codec.pad & 3)] +% codec.table[7];
            }
            """);
        // Task #78: the array fields are copied in by a synthesized initializer (a C# object initializer cannot set them);
        // task #75: `"ABCD".*` is the array itself, `@splat` fills a `[N]T`, and an index takes a cast builtin's type.
        cs.ShouldContain("Codec codec = __init_codec();");
        cs.ShouldContain("memcpy(__anf0.chars, ");
        cs.ShouldContain("stackalloc byte[]{ 255, 255, 255, 255, 255, 255, 255, 255 }");
    }

    [Fact]
    public void A_catch_capture_may_return_a_switch_over_the_error()
    {
        var cs = EmitZig("""
            const E = error{ Truncated, BadStart, Other };
            fn step(x: u8) E!u8 {
                return switch (x) {
                    0 => error.Truncated,
                    1 => error.BadStart,
                    2 => error.Other,
                    else => x * 2,
                };
            }
            fn code(x: u8) u32 {
                const v = step(x) catch |e| return switch (e) {
                    error.Truncated => 900,
                    error.BadStart => 901,
                    else => 902,
                };
                return v;
            }
            pub fn main() u8 {
                return @truncate(code(0) + code(1) + code(2) + code(7));
            }
            """);
        // Task #80 (grammar): `catch |e| return switch (e) { … }`.
        cs.ShouldContain("return (e switch { 1 => 900, 2 => 901, _ => 902 });");
    }

    [Fact]
    public void A_comptime_labeled_block_is_a_static_table()
    {
        var cs = EmitZig("""
            fn classify(c: u8) u8 {
                const xx = 0xF1;
                const as = 0xF0;
                const first = comptime first: {
                    const a: [4]u8 = @splat(as);
                    const b: [4]u8 = @splat(xx);
                    break :first a ++ b;
                };
                return first[c & 7] +% (@as(u8, 1) << @intCast(c & 3));
            }
            pub fn main() u8 {
                return classify(1) +% classify(6);
            }
            """);
        // Task #82: `comptime first: { … }` runs in the comptime interpreter (arrays `++`-joined element by element) into
        // a static, and a shift amount takes a cast builtin's type.
        cs.ShouldContain("__ctblk0 = Libc.GlobalArrayFrom<byte>(new byte[]{ (byte)240, (byte)240, (byte)240, (byte)240, (byte)241, (byte)241, (byte)241, (byte)241 });");
    }

    [Fact]
    public void A_switch_over_a_comptime_bool_selects_its_prong()
    {
        var cs = EmitZig("""
            fn id(comptime T: type, x: T) T {
                return x;
            }
            fn widen(comptime wide: bool, v: u8) u32 {
                const W = switch (wide) {
                    true => u32,
                    false => u8,
                };
                const k: u32 = switch (wide) {
                    true => 1000,
                    false => 1,
                };
                return @as(u32, id(W, v)) + @as(u32, @sizeOf(W)) * k;
            }
            pub fn main() u8 {
                return @truncate(widen(true, 7) + widen(false, 9));
            }
            """);
        // Task #84: each instance folds the switch, as a type alias and as a value.
        cs.ShouldContain("uint k = 1000;");
        cs.ShouldContain("return (uint)id__u32(v)");
        cs.ShouldContain("uint k = 1;");
        cs.ShouldContain("return (uint)id__u8(v)");
    }

    [Fact]
    public void Inline_prongs_type_switch_captures_and_string_literal_anytype_lengths()
    {
        var cs = EmitZig("""
            fn tail(n: usize, bytes: []const u8) u32 {
                var acc: u32 = 7;
                switch (n) {
                    inline 0, 1, 2 => |count| {
                        inline for (0..count) |i| acc = acc *% 31 +% bytes[i];
                        return acc;
                    },
                    inline 3...5 => |count| {
                        acc +%= @as(u32, count) * 1000;
                        inline for (0..count) |i| acc +%= bytes[i];
                        return acc;
                    },
                    else => return 0,
                }
            }
            fn length(x: anytype) usize {
                return x.len;
            }
            fn Widened(comptime T: type) type {
                return switch (T) {
                    comptime_int => u64,
                    else => |U| U,
                };
            }
            pub fn main() u8 {
                const w: Widened(u16) = 40000;
                return @truncate(tail(4, "abcdef") +% @as(u32, @intCast(length("hello"))) +% w);
            }
            """);
        // Task #87: one section per inline case value, its capture a comptime constant; a string literal passed as an
        // `anytype` is its logical `[5]u8` (the stored NUL excluded, it had been 6). Task #86: `else => |U| U` over a type.
        cs.ShouldContain("case 5UL:");
        cs.ShouldContain("length__char_5_(Libc.L(\"hello\\0\"u8))");
        cs.ShouldContain("ushort w = 40000;");
    }

    [Fact]
    public void Extern_struct_value_loops_and_a_struct_const_with_a_computed_array_field()
    {
        var cs = EmitZig("""
            const Kind = union(enum) { null, undefined, int: u8 };
            fn isPow2(v: u16) bool {
                return v != 0 and (v & (v - 1)) == 0;
            }
            fn Words(comptime T: type) type {
                const info = @typeInfo(T);
                if (info != .int) @compileError("an integer");
                if (!isPow2(@bitSizeOf(T))) @compileError("a power of two");
                return extern struct {
                    const Self = @This();
                    const last = ~@as(u64, 0) >> 42;
                    w: [2]T,
                    const full: Self = full: {
                        var w: [2]T = @splat(~@as(T, 0));
                        w[1] = last;
                        break :full .{ .w = w };
                    };
                    fn first(self: Self) ?usize {
                        const word = for (self.w) |word| {
                            if (word != 0) break word;
                        } else return null;
                        return @ctz(word);
                    }
                    fn same(self: Self, other: Self) bool {
                        var i: usize = 0;
                        return while (i < 2) : (i += 1) {
                            if (self.w[i] != other.w[i]) break false;
                        } else true;
                    }
                };
            }
            pub fn main() u8 {
                const k = Kind{ .int = 3 };
                const W = Words(u64);
                const f = W.full;
                return @truncate(k.int + (f.first() orelse 9) + @intFromBool(f.same(f)) + @popCount(f.w[1]));
            }
            """);
        // Task #88 (std.bit_set.Array's shapes). `return extern struct` keeps the C layout; `null`/`undefined` are
        // variant names (the tag member escaped for C#); a `for … else return null` value loop returns from the function
        // on normal completion.
        cs.ShouldContain("LayoutKind.Sequential)]\nunsafe struct Words__u64");
        cs.ShouldContain("@null = 0,");
        cs.ShouldContain("goto __lv0_end;");
        // Two silent miscompiles: `~@as(u64, 0) >> 42` had sign-extended to all ones, and the labeled block's struct had
        // lost its array field (the copy-in ran before the block's locals, and the interpreter skipped it). The const is
        // now built by a synthesized initializer that copies the evaluated array in.
        cs.ShouldContain("Words__u64__full__static = __init_Words__u64__full();");
        cs.ShouldContain("new ulong[]{ 18446744073709551615UL, 4194303UL }");
    }

    [Fact]
    public void Asm_in_a_dead_prong_comptime_array_params_and_implicit_comptime_int_params()
    {
        var cs = EmitZig("""
            const builtin = @import("builtin");

            const Seed = [4]u32;
            const seed_a = Seed{ 3, 5, 7, 11 };

            const Param = struct { a: usize, k: u32 };

            fn Mixer(comptime seed: Seed, rounds: comptime_int) type {
                return struct {
                    const Self = @This();
                    s: [4]u32 align(16),
                    count: u32 align(8) = 0,

                    fn init() Self {
                        return .{ .s = seed };
                    }

                    fn step(self: *Self) void {
                        const params = comptime [_]Param{ .{ .a = 0, .k = 1 }, .{ .a = 2, .k = 3 } };
                        inline for (params) |p| {
                            self.s[p.a] = self.s[p.a] *% 31 +% p.k;
                        }
                        switch (builtin.cpu.arch) {
                            .x86_64, .aarch64 => if (builtin.zig_backend == .stage2_c) {
                                asm volatile ("nop");
                                return;
                            },
                            else => {},
                        }
                        self.count += rounds;
                    }
                };
            }

            pub fn main() u8 {
                var m = Mixer(seed_a, 5).init();
                m.step();
                m.step();
                return @truncate(m.s[0] +% m.s[2] +% m.count);
            }
            """);
        // Task #90 (std.crypto.sha2's shapes). `seed: Seed` (a `[4]u32` alias) is a comptime ARRAY argument read from a
        // const global; `rounds: comptime_int` is comptime without the keyword; `align(16)` fields parse; `inline for`
        // over a comptime array value binds each element as a literal; the `asm` sits in a prong whose condition folds false.
        cs.ShouldContain("public fixed uint s[4];");
        cs.ShouldContain("stackalloc uint[]{ 3u, 5u, 7u, 11u };");
        cs.ShouldContain("Param p = new Param { a = 0UL, k = 1u };");
        cs.ShouldContain("_5_step(&m);");
    }

    [Theory]
    [InlineData("const m = ~@as(u64, 0) >> 88; return @truncate(m);", "type 'u6' cannot represent integer value '88'")]
    [InlineData("var b = true; _ = &b; return @intFromBool(b) * 10;", "type 'u1' cannot represent integer value '10'")]
    [InlineData("var x: u8 = 3; _ = &x; return x + 300;", "type 'u8' cannot represent integer value '300'")]
    [InlineData("var x: i8 = -3; _ = &x; return @bitCast(x - 128);", "type 'i8' cannot represent integer value '128'")]
    // Task #102: a `@clz` / `@ctz` / `@popCount` count is a `Log2IntCeil(T)` (a `u3`'s a `u2`, a `u64`'s a `u7`).
    [InlineData("var s: u3 = 1; _ = &s; return @clz(s) * 100;", "type 'u2' cannot represent integer value '100'")]
    [InlineData("var w: u64 = 1; _ = &w; return @popCount(w) + 200;", "type 'u7' cannot represent integer value '200'")]
    public void A_comptime_operand_its_type_cannot_hold_is_rejected_as_zig_does(string body, string message)
    {
        // Task #91: a shift amount outside `Log2Int` of the shifted operand, and an integer literal outside its typed peer's
        // range, are zig compile errors (the messages are zig's own); dotcc had lowered both silently.
        Should.Throw<CompileException>(() => EmitZig("pub fn main() u8 {\n    " + body + "\n}\n")).Message.ShouldContain(message);
    }

    [Theory]
    [InlineData("var x: u64 = 3; _ = &x; return @truncate(x >> 63);")]
    [InlineData("const k = 1 << 40; return @truncate(k >> 33);")]
    [InlineData("var b = true; _ = &b; return @as(u8, @intFromBool(b)) * 10;")]
    // Task #102: the count is UNSIGNED, so a `u2` holds 1 (an `i2` would not); widened, it holds anything.
    [InlineData("var s: u3 = 1; _ = &s; return @clz(s) + 1;")]
    [InlineData("var s: u3 = 1; _ = &s; return @as(u8, @clz(s)) * 100;")]
    public void A_comptime_operand_that_fits_or_is_comptime_int_is_accepted(string body)
    {
        Should.NotThrow(() => EmitZig("pub fn main() u8 {\n    " + body + "\n}\n"));
    }

    [Fact]
    public void A_comptime_block_returning_a_local_slice_is_a_static_of_its_evaluated_elements()
    {
        var cs = EmitZig("""
            const Color = enum(u8) { red = 4, green = 9, blue = 2 };

            inline fn table(comptime fv: []const comptime_int) []const u8 {
                comptime {
                    var result: [fv.len]u8 = undefined;
                    for (&result, fv) |*r, f| {
                        r.* = @intCast(f * 2);
                    }
                    const final = result;
                    return &final;
                }
            }

            inline fn colors(comptime fv: []const comptime_int) []const Color {
                comptime {
                    var result: [fv.len]Color = undefined;
                    for (&result, fv) |*r, f| {
                        r.* = @enumFromInt(f);
                    }
                    const final = result;
                    return &final;
                }
            }

            pub fn main() u8 {
                const t = table(&.{ 5, 6, 7 });
                const c = colors(&.{ 2, 9 });
                return t[0] + t[1] + t[2] + @intFromEnum(c[0]) * 10 + @intFromEnum(c[1]);
            }
            """);
        // Task #89 (std.enums.valuesFromFields' shape): the `comptime { …; return &final; }` block runs in the interpreter and
        // its slice becomes a pinned static. Two silent miscompiles: lowered as runtime code it returned a slice over the
        // frame's `stackalloc` (dangling), and the interpreter had skipped the `const final = result;` array copy (all zeros).
        cs.ShouldContain("return new ConstSlice<byte>(Libc.L(\"\\n\\x0C\\x0E\\0\"u8), 3UL);");
        cs.ShouldContain("new Color[]{ (Color)(byte)2, (Color)(byte)9 }");
        cs.ShouldNotContain("stackalloc Color");
    }

    [Fact]
    public void A_non_exhaustive_enum_has_no_underscore_member_and_reports_its_mode()
    {
        var cs = EmitZig("""
            const E = enum(u8) { a, b, _ };

            pub fn main() u8 {
                const info = @typeInfo(E).@"enum";
                const open: u8 = if (info.mode == .nonexhaustive) 10 else 0;
                const e: E = @enumFromInt(7);
                return open + @as(u8, info.field_names.len) + @intFromEnum(e);
            }
            """);
        // Task #89: `_` marks the enum non-exhaustive; it had been lowered as a member `_ = 2`, so `field_names.len` said 3.
        cs.ShouldNotContain("_ = 2,");
        cs.ShouldContain("byte open = 10;");
        cs.ShouldContain("return (byte)(open + (byte)2 + (byte)e);");
    }

    [Fact]
    public void Wide_comptime_int_arithmetic_folds_and_comptime_case_labels_fold_to_the_subject()
    {
        var cs = EmitZig("""
            fn classify(x: usize) u8 {
                const limit = 4 * 3;
                return switch (x) {
                    limit => 2,
                    else => 3,
                };
            }

            fn pick() u8 {
                if (@inComptime()) return 1;
                return 2;
            }

            pub fn main() u8 {
                const big = 0xFFFF_FFFF_FFFF_FFFF + 1;
                const top: u64 = @intCast(big >> 60);
                return @intCast(top + classify(12) + pick());
            }
            """);
        // Task #83: `maxInt(u64) + 1` is a comptime_int; it had wrapped at the 64-bit carrier to 0.
        cs.ShouldContain("System.Int128 big = System.Int128.Parse(\"18446744073709551616\");");
        // A comptime-const case label folds to a literal at the subject's type.
        cs.ShouldContain("(x switch { 12UL => 2, _ => 3 })");
        // `@inComptime()` is false in a function lowered for runtime.
        cs.ShouldContain("if (Cond.B(false))");
    }

    [Fact]
    public void Struct_builtin_reifies_one_struct_per_instance_with_its_default_values()
    {
        var cs = EmitZig("""
            fn FieldStruct(comptime Data: type, comptime def: ?Data) type {
                const default_ptr: ?*const anyopaque = if (def) |d| @ptrCast(&d) else null;
                return @Struct(.auto, null, &.{ "a", "b", "c" }, &@splat(Data), &@splat(.{ .default_value_ptr = default_ptr }));
            }

            fn Pair(comptime A: type, comptime B: type) type {
                return @Struct(.auto, null, &.{ "x", "y" }, &.{ A, B }, &.{ .{}, .{} });
            }

            fn sum(s: FieldStruct(u8, 7)) u8 {
                return s.a + s.b * 10 + s.c;
            }

            pub fn main() u8 {
                const t: FieldStruct(bool, null) = .{ .a = true, .b = false, .c = true };
                const p: Pair(u8, u16) = .{ .x = 2, .y = 300 };
                const n: u8 = if (t.a and !t.b and t.c) 100 else 0;
                return sum(.{ .b = 3 }) + n + p.x + @as(u8, @intCast(p.y - 290));
            }
            """);
        // Task #93: `@Struct` as a type-returning function's result is a struct named after the instance; a literal
        // omitting a field is filled from the `.default_value_ptr` default (7), and a null default leaves none.
        cs.ShouldContain("new FieldStruct__u8_opt7 { b = 3, a = 7, c = 7 }");
        cs.ShouldContain("new FieldStruct__bool_optnull { a = true, b = false, c = true }");
        cs.ShouldContain("unsafe struct Pair__u8_u16");
        cs.ShouldContain("public ushort y;");
    }

    [Theory]
    [InlineData("&.{ \"a\", \"a\" }", "&@splat(u8)", "&@splat(.{})", "duplicate struct field name 'a'")]
    [InlineData("&.{ \"a\", \"b\" }", "&.{ u8 }", "&@splat(.{})", "field_types has 1 element(s) but there are 2 field name(s)")]
    [InlineData("&.{ \"a\", \"b\" }", "&@splat(u8)", "&@splat(.{ .@\"comptime\" = true })", "a `comptime` field is not modeled")]
    public void Struct_builtin_rejects_a_malformed_field_list(string names, string types, string attrs, string message)
    {
        // Task #93: zig rejects a duplicate name and a list whose length is not the name count; a comptime field is a cut.
        var ex = Should.Throw<Exception>(() => EmitZig(
            "fn S() type {\n    return @Struct(.auto, null, " + names + ", " + types + ", " + attrs + ");\n}\n"
            + "pub fn main() u8 {\n    const s: S() = undefined;\n    _ = s;\n    return 0;\n}\n"));
        ex.Message.ShouldContain(message);
    }

    [Fact]
    public void Tag_name_of_a_comptime_enum_value_is_its_member_name()
    {
        var cs = EmitZig("""
            const E = enum { a, bb, ccc };
            fn keyFor(i: usize) E {
                return @enumFromInt(i);
            }
            pub fn main() u8 {
                var total: u8 = 0;
                inline for (0..3) |i| {
                    const key = comptime keyFor(i);
                    const tag = @tagName(key);
                    total += @intCast(tag.len * 10 + i);
                }
                return total + @as(u8, @intCast(@tagName(E.bb).len));
            }
            """);
        // Task #93: `@tagName` of a comptime key (`comptime keyFor(i)`) is the member's name as a string literal; the
        // enum-typed fold is cast to the enum (it had been spliced as `E key = 0UL;`, which C# rejects).
        cs.ShouldContain("E key = (E)0;");
        cs.ShouldContain("Libc.L(\"bb\\0\"u8)");
        cs.ShouldContain("Libc.L(\"ccc\\0\"u8)");
    }

    [Fact]
    public void Tag_name_of_a_runtime_enum_value_is_a_loud_cut()
    {
        Should.Throw<Exception>(() => EmitZig("""
            const E = enum { a, b };
            pub fn main() u8 {
                var e: E = .a;
                e = .b;
                return @intCast(@tagName(e).len);
            }
            """)).Message.ShouldContain("`@tagName` is modeled for a comptime-known enum value");
    }

    [Fact]
    public void A_literal_of_another_modules_struct_fills_its_field_defaults()
    {
        // A struct literal in the root of a struct another module declares had dropped every omitted default (dotcc 3,
        // zig 62): the defaults are the declaring module's, and are lowered there.
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zigdef-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "m.zig"),
                "pub const S = struct {\n    a: u8 = 5,\n    b: u8,\n};\n"
                + "pub fn Gen(comptime n: u8) type {\n    return struct { k: u8 = n, z: u8 = 1 };\n}\n");
            var main = Path.Combine(dir, "main.zig");
            File.WriteAllText(main,
                "const m = @import(\"m.zig\");\npub fn main() u8 {\n    const s: m.S = .{ .b = 1 };\n"
                + "    const g: m.Gen(9) = .{ .z = 2 };\n    return s.a * 10 + s.b + g.k + g.z;\n}\n");
            var cs = Compiler.EmitCSharp(new[] { main });
            cs.ShouldContain("new m__S { b = 1, a = 5 }");
            cs.ShouldContain("new m__Gen__9 { z = 2, k = 9 }");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_by_ref_if_capture_points_at_the_payload_in_place()
    {
        var cs = EmitZig("""
            const Holder = struct {
                a: ?u8 = 5,
                b: ?u16 = null,
            };

            fn maybe(n: u8) ?u8 {
                return if (n > 2) n else null;
            }

            pub fn main() u8 {
                var x: ?u8 = 10;
                if (x) |*v| {
                    v.* += 3;
                }
                var h: Holder = .{};
                if (h.a) |*p| {
                    p.* *= 2;
                } else {
                    h.a = 99;
                }
                if (h.b) |*q| {
                    q.* = 1;
                } else {
                    h.b = 7;
                }
                var n: u8 = 4;
                var ptr: ?*u8 = &n;
                if (ptr) |*pp| {
                    pp.*.* += 1;
                    const same: *u8 = pp.*;
                    same.* += 10;
                }
                var seen: u8 = 0;
                if (maybe(9)) |*m| {
                    seen = m.*;
                }
                const got: u8 = if (ptr == null) 1 else 0;
                return x.? + h.a.? + @as(u8, @intCast(h.b.?)) + n + seen + got;
            }
            """);
        // Task #94: `if (opt) |*v|` points `v` INTO the optional (no copy, no write-back), an optional pointer's capture
        // is the address of the pointer variable, and an rvalue condition is a temporary the capture points into.
        cs.ShouldContain("byte* v = ZigMem.OptionalPayload(&x);");
        cs.ShouldContain("byte* p = ZigMem.OptionalPayload(&h.a);");
        cs.ShouldContain("byte** pp = &ptr;");
        cs.ShouldContain("byte* m = ZigMem.OptionalPayload(&__cap);");
    }

    [Fact]
    public void A_by_ref_capture_of_an_error_union_is_a_loud_cut()
    {
        Should.Throw<Exception>(() => EmitZig("""
            fn get(ok: bool) !u8 {
                return if (ok) 3 else error.Nope;
            }
            pub fn main() u8 {
                var total: u8 = 0;
                if (get(true)) |*v| {
                    total = v.*;
                }
                return total;
            }
            """)).Message.ShouldContain("a by-ref capture of an error union's payload is not modeled");
    }

    [Theory]
    [InlineData("const y: u8 = 1;\n    y = 2;\n    return y;")]
    [InlineData("const y: u8 = 1;\n    y += 2;\n    return y;")]
    [InlineData("const y: u8 = 1;\n    const p = &y;\n    p.* = 2;\n    return y;")]
    [InlineData("const s: S = .{ .a = 1 };\n    s.a = 2;\n    return s.a;")]
    [InlineData("const arr = [_]u8{ 1, 2 };\n    arr[0] = 5;\n    return arr[0];")]
    [InlineData("const x: ?u8 = 1;\n    if (x) |*v| {\n        v.* = 2;\n    }\n    return x.?;")]
    [InlineData("return f(1);")]
    [InlineData("var y: u8 = 1;\n    g(&y);\n    return y;")]
    public void A_store_to_a_const_is_rejected_as_in_zig(string body)
    {
        // Task #95: zig's "cannot assign to constant" for a `const` local, a parameter, a field or element of one, and a
        // store through a pointer to const (`&y` of a const `y` is a `*const T`; so is a by-ref capture of a const).
        Should.Throw<CompileException>(() => EmitZig(
            "const S = struct { a: u8 };\n"
            + "fn f(x: u8) u8 {\n    x = 2;\n    return x;\n}\n"
            + "fn g(p: *const u8) void {\n    p.* = 2;\n}\n"
            + "pub fn main() u8 {\n    " + body + "\n}\n")).Message.ShouldContain("cannot assign to constant");
    }

    [Fact]
    public void A_store_through_a_pointer_to_mutable_storage_still_lowers()
    {
        // Task #95: a `const` binding that HOLDS a pointer (`const p: *[2]u32 = &b;`), a pointer parameter, a `var`, and a
        // by-ref capture of a `var` optional all write their pointee, as in zig (this program returns 10 there).
        var cs = EmitZig("""
            fn fill(buf: *[2]u32, v: u32) void {
                buf[1] = v;
            }
            pub fn main() u8 {
                var b = [2]u32{ 0, 0 };
                const p: *[2]u32 = &b;
                p[1] = 3;
                const q = &b;
                q[0] = 4;
                p.*[0] += 1;
                fill(&b, 3);
                var o: ?u8 = 1;
                if (o) |*v| {
                    v.* += 1;
                }
                return @intCast(b[0] + b[1] + o.?);
            }
            """);
        cs.ShouldContain("buf[1] = v;");
        cs.ShouldContain("ZigMem.OptionalPayload(&o)");
    }

    [Fact]
    public void A_pointer_to_a_container_type_is_a_comptime_namespace_argument()
    {
        var cs = EmitZig("""
            const Small = struct {
                const T = u64;
                const bound = 21;
                fn scale(i: u32) u32 {
                    return i * 2;
                }
            };
            const Full = struct {
                const T = u64;
                const bound = 40;
                fn scale(i: u32) u32 {
                    return i * 3;
                }
            };

            fn use(comptime T: type, x: u32, comptime tables: anytype) u32 {
                if (T != tables.T) @compileError("table type mismatch");
                return tables.scale(x) + tables.bound;
            }

            fn check(ok: bool) void {
                if (!ok) unreachable;
            }

            fn Checked(comptime n: u8) type {
                comptime check(n > 1);
                return struct {
                    v: u8 = n,
                };
            }

            pub fn main() u8 {
                const small = true;
                const tables = if (!small) &Small else &Full;
                const t: u32 = use(u64, 3, tables) + use(u64, 1, &Small);
                const a: u8 = 3;
                const b: u8 = if (a != if (a > 2) @as(u8, 3) else 0) 7 else 9;
                const c: Checked(4) = .{};
                return @intCast(t + b + c.v);
            }
            """);
        // Task #85: `comptime tables: anytype` fed `&Full` (or a const bound to a folded `if` of type pointers) is a comptime
        // TYPE seed, one instance per container and no runtime slot; `x != if (c) a else b` is a comparison operand; a type
        // body's `comptime check(…);` evaluates to void.
        cs.ShouldContain("uint t = use__u64_tpFull(3) + use__u64_tpSmall(1);");
        cs.ShouldContain("internal static unsafe uint use__u64_tpFull(uint x)");
        cs.ShouldContain("? (byte)3 : (byte)(0)");
        cs.ShouldContain("Checked__4 c = new Checked__4 { v = 4 };");
    }

    [Fact]
    public void An_if_arm_assignment_a_flat_nested_table_and_a_literal_catch_fallback()
    {
        var cs = EmitZig("""
            const TABLE: [3][2]u64 = .{
                .{ 1, 2 },
                .{ 3, 4 },
                .{ 5, 6 },
            };

            fn row(i: u32) [2]u64 {
                return TABLE[i];
            }

            fn pick(ok: bool, buf: []u8) error{Nope}![]u8 {
                if (!ok) return error.Nope;
                return buf[0..2];
            }

            pub fn main() u8 {
                var i: u32 = 10;
                var j: u32 = 1;
                for (0..4) |k| {
                    if (k % 2 == 0) i += 3 else i -= 1;
                    if (k == 3) j = 7 else j *= 2;
                    if (k > 5) j <<= 1 else j |= 1;
                }
                var buf = [_]u8{ 'a', 'b', 'c' };
                const good = pick(true, &buf) catch "ERR";
                const bad = pick(false, &buf) catch "ERR";
                const r = row(2);
                return @intCast(i + j + good.len * 10 + bad.len + r[0] * r[1] + TABLE[1][0]);
            }
            """);
        // Task #96: `if (c) i += 3 else i -= 1;` (an assignment then-arm ended by the `else`) lowers as the statements it
        // spells; a nested `[3][2]u64` const is ONE flat pinned block (its rows had been `stackalloc` pointers inside the
        // static initializer); a `catch "ERR"` fallback is result-located at the payload slice (it had stayed a `byte*`).
        cs.ShouldContain("i += (uint)(3);");
        cs.ShouldContain("j = 7;");
        cs.ShouldContain("j <<= 1;");
        cs.ShouldContain("public static unsafe ulong* TABLE = Libc.GlobalArrayFrom<ulong>(new ulong[]{ 1, 2, 3, 4, 5, 6 });");
        cs.ShouldContain("? new Slice<byte>(Libc.L(\"ERR\\0\"u8), 3UL) :");
    }

    [Theory]
    [InlineData("return seven();")]
    [InlineData("return viaRuntime();")]
    [InlineData("const s = S{ .v = 1 };\n    return s.v + S.get();")]
    public void A_runtime_call_of_a_comptime_returning_function_is_rejected(string body)
    {
        // Task #92: zig's "function called at runtime cannot return value at comptime", for a plain (non-inline) function
        // whose `comptime { return … }` block is reached by a runtime call: directly from `main`, through another
        // function, or as a container method.
        Should.Throw<CompileException>(() => EmitZig(
            "fn seven() u8 {\n    comptime {\n        return 7;\n    }\n}\n"
            + "fn viaRuntime() u8 {\n    return seven() + 1;\n}\n"
            + "const S = struct {\n    v: u8,\n    fn get() u8 {\n        comptime {\n            return 2;\n        }\n    }\n};\n"
            + "pub fn main() u8 {\n    " + body + "\n}\n")).Message.ShouldContain("function called at runtime cannot return value at comptime");
    }

    [Fact]
    public void A_comptime_returning_function_is_legal_at_compile_time_inline_or_unreferenced()
    {
        // Task #92: the same block is legal in an `inline fn`, under `comptime f()`, from a function only a comptime call
        // reaches, in a top-level initializer, and in a function nothing calls (zig never analyzes it). zig returns 27.
        var cs = EmitZig("""
            fn seven() u8 {
                comptime {
                    return 7;
                }
            }

            inline fn five() u8 {
                comptime {
                    return 5;
                }
            }

            fn viaHelper() u8 {
                return seven() + 1;
            }

            fn neverCalled() u8 {
                return seven();
            }

            const top = seven();

            pub fn main() u8 {
                const a = comptime seven();
                const b = comptime viaHelper();
                return a + b + top + five();
            }
            """);
        cs.ShouldContain("seven()");
    }

    [Fact]
    public void A_narrow_complement_and_wrapping_op_stay_at_the_declared_width()
    {
        var cs = EmitZig("""
            fn rotl8(x: u8, r: u3) u8 {
                return x << r | x >> 1 +% ~r;
            }

            pub fn main() u8 {
                var x: u8 = 1;
                x += 0;
                var h: u16 = 0x00f0;
                h += 0;
                var small: u5 = 3;
                small += 0;
                var total: u32 = 0;
                if (~x == 254) total += 1;
                if (~h == 0xff0f) total += 2;
                const ns: u5 = ~small;
                total += ns;
                const w: u5 = small -% 5;
                total += w;
                const m: u3 = @as(u3, 5) *% 3;
                total += m;
                total += rotl8(0b1000_0001, 1);
                total += rotl8(0b0100_0000, 3);
                return @intCast(total);
            }
            """);
        // Task #97 (silent miscompiles): `~x` of a u8 / u16 had been C#'s promoted `int` (`~1` == -2, so `~x == 254` was
        // false), and a `u3` / `u5` complement or wrapping op had wrapped at its byte carrier: std.math.rotl(u8, 0x81, 1)'s
        // `x >> 1 +% ~ar` became `x >> -1` (masked by C# to 31), giving 2 where zig gives 3. zig returns 73 here.
        cs.ShouldContain("return (byte)(x << (int)(r) | x >> (int)((byte)(1 + (byte)(~r & 7) & 7)));");
        cs.ShouldContain("(byte)~x == 254");
        cs.ShouldContain("(ushort)~h == 65295");
        cs.ShouldContain("byte ns = (byte)(~small & 31);");
    }

    [Theory]
    [InlineData("var a: i32 = 7;\n    a += 0;\n    return @intCast(a / 2);", "signed integers must use @divTrunc, @divFloor, or @divExact")]
    [InlineData("var a: i32 = 7;\n    a += 0;\n    return @intCast(a % 3);", "signed integers and floats must use @rem or @mod")]
    [InlineData("var a: i32 = 7;\n    a += 0;\n    a /= 2;\n    return @intCast(a);", "signed integers must use @divTrunc, @divFloor, or @divExact")]
    [InlineData("var f: f32 = 7;\n    f += 0;\n    return @intFromFloat(f % 2);", "signed integers and floats must use @rem or @mod")]
    // Task #103: the ZIG peer type decides, not C#'s promotion: `(i * 3) % 5` over an `i16` is still signed.
    [InlineData("var i: i16 = 7;\n    i += 0;\n    return @intCast((i * 3) % 5);", "signed integers and floats must use @rem or @mod")]
    public void Division_of_a_runtime_signed_integer_needs_an_explicit_rounding(string body, string message)
    {
        // Task #98: zig rejects `/` and `%` on a signed integer (and `%` on a float) unless both operands are comptime-known.
        Should.Throw<CompileException>(() => EmitZig("pub fn main() u8 {\n    " + body + "\n}\n")).Message.ShouldContain(message);
    }

    [Fact]
    public void The_explicit_division_builtins_and_unsigned_or_comptime_division_lower()
    {
        // Task #98: @divTrunc / @divFloor / @mod / @rem, unsigned `/` and `%`, float `/`, a float `@mod` (a new ZigMath overload:
        // it had been sent to the integer-only generic, CS0315) and comptime-known signed `/` all compile. zig returns 19.
        var cs = EmitZig("""
            pub fn main() u8 {
                var a: i32 = -7;
                a += 0;
                var u: u32 = 17;
                u += 0;
                var f: f32 = 9.0;
                f += 0;
                const k: i32 = -9;
                var total: i32 = 0;
                total += @divTrunc(a, 2);
                total += @divFloor(a, 2);
                total += @mod(a, 3);
                total += @rem(a, 3);
                total += @intCast(u / 4 + u % 5);
                total += @intFromFloat(f / 2.0);
                total += @intFromFloat(@mod(f, 4.0));
                total += k / 3;
                total += -12 / 4;
                return @intCast(total + 20);
            }
            """);
        cs.ShouldContain("total += (int)ZigMath.Mod((double)f, 4.0);");
        cs.ShouldContain("total += ZigMath.DivFloor(a, 2);");
    }

    [Fact]
    public void A_type_returning_generic_takes_a_comptime_function_argument()
    {
        var cs = EmitZig("""
            fn sameLen(a: []const u8, b: []const u8) bool {
                return a.len == b.len;
            }

            fn exact(a: []const u8, b: []const u8) bool {
                if (a.len != b.len) return false;
                for (a, b) |x, y| {
                    if (x != y) return false;
                }
                return true;
            }

            fn Matcher(comptime V: type, comptime eql: fn (a: []const u8, b: []const u8) bool) type {
                return struct {
                    key: []const u8,
                    val: V,

                    fn get(self: @This(), k: []const u8) ?V {
                        return if (eql(self.key, k)) self.val else null;
                    }
                };
            }

            fn count(comptime t: anytype) usize {
                return t.len;
            }

            pub fn main() u8 {
                const loose: Matcher(u8, sameLen) = .{ .key = "abc", .val = 7 };
                const strict: Matcher(u8, exact) = .{ .key = "abc", .val = 9 };
                var total: usize = 0;
                total += loose.get("xyz") orelse 0;
                total += strict.get("xyz") orelse 100;
                total += strict.get("abc") orelse 0;
                total += count(.{ 1, 2, 3 }) * 10;
                return @intCast(total);
            }
            """);
        // Task #99: `comptime eql: fn (…) bool` on a type-returning generic (std.StaticStringMapWithEql) keys one struct per
        // function, and each reified method calls the function it was given; a tuple's `.len` is its element count.
        // zig returns 146.
        cs.ShouldContain("Matcher__u8_fnsameLen loose = new Matcher__u8_fnsameLen");
        cs.ShouldContain("Matcher__u8_fnexact strict = new Matcher__u8_fnexact");
        cs.ShouldContain("exact(self.key, k)");
        cs.ShouldContain("sameLen(self.key, k)");
    }

    [Fact]
    public void Clz_and_ctz_of_an_arbitrary_width_integer_count_within_its_declared_width()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                var z: u5 = 5;
                z += 0;
                var q: u12 = 0x0f0;
                q += 0;
                var s: u3 = 1;
                s += 0;
                var zero: u5 = 0;
                zero += 0;
                var total: u32 = 0;
                total += @clz(z);
                total += @as(u32, @clz(q)) * 10;
                total += @as(u32, @clz(s)) * 100;
                total += @ctz(zero);
                total += @clz(zero);
                total += @clz(@as(u5, 3));
                return @intCast(total % 256);
            }
            """);
        // Task #101 (silent miscompile): `@clz` of a `u5` / `u12` / `u3` counted the byte / ushort carrier's leading zeros
        // (`@clz(@as(u3, 1))` was 7, zig 2); `@ctz` of a zero `u5` said 8. zig returns 255 here.
        cs.ShouldContain("total += (uint)((ZigMath.Clz(z) - 3));");
        cs.ShouldContain("total += (uint)(System.Math.Min(ZigMath.Ctz(zero), 5));");
    }

    [Fact]
    public void A_comptime_function_argument_reaches_a_nested_container_of_the_instance()
    {
        var cs = EmitZig("""
            const E = error{ Full, Empty };

            fn less(_: void, a: u16, b: u16) bool {
                return a < b;
            }

            fn Heap(comptime T: type, comptime Context: type, comptime lessFn: fn (context: Context, a: T, b: T) bool) type {
                return struct {
                    items: [8]T = undefined,
                    len: usize = 0,
                    context: Context = undefined,
                    const Self = @This();
                    pub const Cursor = struct {
                        heap: *Heap(T, Context, lessFn),
                        at: usize,
                        pub fn next(c: *Cursor) ?T {
                            if (c.at >= c.heap.len) return null;
                            c.at += 1;
                            return c.heap.items[c.at - 1];
                        }
                    };
                    fn put(self: *Self, v: T) E!void {
                        if (self.len == self.items.len) return error.Full;
                        var i = self.len;
                        self.items[i] = v;
                        self.len += 1;
                        while (i > 0 and lessFn(self.context, self.items[i], self.items[i - 1])) : (i -= 1) {
                            const t = self.items[i];
                            self.items[i] = self.items[i - 1];
                            self.items[i - 1] = t;
                        }
                    }
                    fn cursor(self: *Self) Cursor {
                        return .{ .heap = self, .at = 0 };
                    }
                };
            }

            fn fill(h: *Heap(u16, void, less), n: u16) u16 {
                var i: u16 = 0;
                while (i < n) : (i += 1) {
                    h.put((i * 37) % 101) catch |e| switch (e) {
                        error.Full => {
                            return i;
                        },
                        error.Empty => unreachable,
                    };
                }
                return n;
            }

            pub fn main() u8 {
                var h: Heap(u16, void, less) = .{};
                const stored = fill(&h, 20);
                var c = h.cursor();
                var sum: u32 = 0;
                var prev: u16 = 0;
                var sorted = true;
                while (c.next()) |v| {
                    if (v < prev) sorted = false;
                    prev = v;
                    sum += v;
                }
                if (!sorted) return 1;
                return @intCast(stored * 10 + sum % 97);
            }
            """);
        // Task #103 (std.PriorityQueue): the nested `Cursor`'s field `*Heap(T, Context, lessFn)` passes the instance's comptime
        // function along, so the alias stays live for the whole reification, not just the body walk; a value switch takes a
        // block prong that always returns (`error.Full => { return i; }`); and `(i * 37) % 101` over a `u16` is unsigned
        // `%`, whatever C#'s promotion makes of it. zig returns 118.
        cs.ShouldContain("public Heap__u16_void_fnless* heap;");
        cs.ShouldContain("Heap__u16_void_fnless_put(h, (ushort)(i * 37 % 101))");
        cs.ShouldContain("return i;");
    }

    [Fact]
    public void A_value_switch_block_prong_that_can_complete_is_still_rejected()
    {
        // Task #103: only a block that never completes may stand where a switch expression needs a value.
        Should.Throw<CompileException>(() => EmitZig("""
            pub fn main() u8 {
                var v: u8 = 1;
                _ = &v;
                const x: u8 = switch (v) {
                    0 => {
                        v += 1;
                    },
                    else => 1,
                };
                return x;
            }
            """)).Message.ShouldContain("a void block prong");
    }

    [Fact]
    public void A_while_capture_continue_expression_reads_the_capture()
    {
        var cs = EmitZig("""
            const Node = struct {
                v: u32,
                next: ?*Node = null,
            };

            fn step(x: u8) ?u8 {
                return if (x >= 40) null else x + 7;
            }

            pub fn main() u8 {
                var c = Node{ .v = 9 };
                var b = Node{ .v = 5, .next = &c };
                var a = Node{ .v = 3, .next = &b };
                var sum: u32 = 0;
                var it: ?*Node = &a;
                while (it) |n| : (it = n.next) {
                    sum = sum * 10 + n.v;
                }
                var hops: u32 = 0;
                var cur: ?u8 = 1;
                outer: while (cur) |x| : (cur = step(x)) {
                    hops += 1;
                    if (x % 2 == 0) continue :outer;
                    hops += 100;
                }
                var skipped: u32 = 0;
                var it2: ?*Node = &a;
                while (it2) |n| : (it2 = n.next) {
                    if (n.v == 5) continue;
                    skipped += n.v;
                }
                return @intCast((sum + hops + skipped) % 256);
            }
            """);
        // Task #105: `while (it) |n| : (it = n.next)` (the std.SinglyLinkedList walk) reads the capture in the continue
        // expression, so the capture is declared in the `for` init, whose scope spans the post and the body, and assigned
        // each turn; a labeled `continue` still lands on the post. zig returns 10.
        cs.ShouldContain("for (Node* n = default(Node*); ; it = n->next)");
        cs.ShouldContain("n = __cap;");
        cs.ShouldContain("for (byte x = default(byte); ; cur = step(x))");
        cs.ShouldContain("goto __loop1_cont;");
    }

    [Fact]
    public void A_comptime_bool_argument_asked_as_a_question_keys_the_instance()
    {
        var cs = EmitZig("""
            fn cheap(comptime K: type) bool {
                return switch (@typeInfo(K)) {
                    .int, .bool => true,
                    else => false,
                };
            }
            fn Box(comptime K: type, comptime store: bool) type {
                return struct {
                    k: K,
                    extra: Extra,
                    pub const Extra = if (store) u32 else u8;
                };
            }
            fn Auto(comptime K: type) type {
                return Box(K, !cheap(K));
            }
            pub fn main() u8 {
                const b: Auto(u16) = .{ .k = 3, .extra = 4 };
                const c: Box(u16, cheap(u16)) = .{ .k = 5, .extra = 6 };
                return @intCast(b.k + b.extra + @sizeOf(@TypeOf(b.extra)) * 10 + c.k + c.extra + @sizeOf(@TypeOf(c.extra)) * 20);
            }
            """);
        // Task #106 (std.array_hash_map.Auto's `!autoEqlIsCheap(K)`): a `comptime store: bool` argument that is a comptime
        // QUESTION (a call switching over `@typeInfo(K)`, negated or not) folds, keys the instance, and never lowers as a
        // runtime call. zig returns 108.
        cs.ShouldContain("Box__u16_0 b = new Box__u16_0");
        cs.ShouldContain("Box__u16_1 c = new Box__u16_1");
        cs.ShouldContain("public uint extra;");
        cs.ShouldNotContain("cheap__");
    }

    [Fact]
    public void Allocator_dupeSentinel_and_allocSentinel_store_the_sentinel_past_the_end()
    {
        var cs = EmitZig("""
            const std = @import("std");
            pub fn main() !u8 {
                var buf: [1024]u8 = undefined;
                var fba = std.heap.FixedBufferAllocator.init(&buf);
                const alloc = fba.allocator();
                const z = try alloc.dupeSentinel(u8, "abcd", 0);
                const w = try alloc.allocSentinel(u16, 3, 0xffff);
                w[0] = 1;
                const h = try std.heap.page_allocator.dupeSentinel(u8, "xy", '!');
                return @intCast(z.len + z[4] + w.len + w[0] + h[2]);
            }
            """);
        // Task #107: `[:s]T` from an allocator is one element more with the sentinel past the end; dotcc's slice carries no
        // sentinel, so the result is the `len`-long slice before it (reading `z[4]` still sees it). The real-std probe with
        // these calls returns 188 on both zig and dotcc.
        cs.ShouldContain("ZigAlloc.DupeSentinel(ZigAlloc.FbaAllocator(&fba), new ConstSlice<byte>(Libc.L(\"abcd\\0\"u8), 4UL), (byte)0, 1)");
        cs.ShouldContain("ZigAlloc.AllocSentinel(ZigAlloc.FbaAllocator(&fba), 3, (ushort)65535, 1)");
        cs.ShouldContain("ZigAlloc.DupeSentinel(ZigAlloc.CHeap(), new ConstSlice<byte>(Libc.L(\"xy\\0\"u8), 2UL), (byte)33, 1)");
    }

    [Fact]
    public void Inline_union_types_lower_as_parameter_field_and_annotation_types()
    {
        var cs = EmitZig("""
            fn weigh(x: union(enum) { small: u8, big: u16, none }) u16 {
                return switch (x) {
                    .small => |s| s,
                    .big => |b| b * 2,
                    .none => 1,
                };
            }

            const Slot = struct {
                tag: union(enum) { n: u8, pair: struct { a: u8, b: u8 } },
                raw: union { word: u16, bytes: [2]u8 },
            };

            pub fn main() u8 {
                var total: u16 = weigh(.{ .small = 7 }) + weigh(.{ .big = 20 }) + weigh(.none);
                const s = Slot{ .tag = .{ .pair = .{ .a = 3, .b = 4 } }, .raw = .{ .word = 0x0102 } };
                const extra: u16 = switch (s.tag) {
                    .n => |v| v,
                    .pair => |p| p.a * p.b,
                };
                total += extra;
                var local: union(enum) { on: u8, off } = .off;
                switch (local) {
                    .off => {
                        total += 5;
                    },
                    .on => {},
                }
                local = .{ .on = 9 };
                switch (local) {
                    .on => |v| {
                        total += v;
                    },
                    .off => {},
                }
                total += s.raw.word & 0xff;
                return @intCast(total);
            }
            """);
        // Task #104: `union(enum) { … }` / `union { … }` inline as a parameter, field or local annotation type is reified per
        // occurrence under an anonymous name, exactly like a named union, so a switch over it and its capture prongs
        // resolve the same way. zig returns 76.
        cs.ShouldContain("internal static unsafe ushort weigh(__AnonUnion3 x)");
        cs.ShouldContain("tag = new __AnonUnion0 { __tag = __AnonUnion0_Tag.pair");
        cs.ShouldContain("raw = new __AnonUnion2 { word = 258 }");
        cs.ShouldContain("__AnonUnion4 local = new __AnonUnion4 { __tag = __AnonUnion4_Tag.off };");
    }

    [Fact]
    public void Tagged_union_switches_take_assignment_prongs_and_tag_comparisons()
    {
        var cs = EmitZig("""
            const Cmd = union(enum) {
                add: u16,
                mul: u16,
                reset,
            };

            fn apply(acc: *u16, c: Cmd) void {
                switch (c) {
                    .add => |n| acc.* += n,
                    .mul => |n| acc.* *= n,
                    .reset => acc.* = 1,
                }
            }

            pub fn main() u8 {
                var acc: u16 = 1;
                const cmds = [_]Cmd{ .{ .add = 4 }, .{ .mul = 3 }, .reset, .{ .add = 9 }, .{ .mul = 2 } };
                var resets: u8 = 0;
                for (cmds) |c| {
                    apply(&acc, c);
                    if (c == .reset) resets += 1;
                    if (.add == c) acc += 0;
                }
                var bonus: u16 = 0;
                for (cmds) |c| {
                    bonus += switch (c) {
                        .add => |n| n,
                        .mul => |n| n * 10,
                        .reset => 100,
                    };
                }
                var st: union(enum) { idle, busy: u8 } = .{ .busy = 7 };
                var seen: u16 = 0;
                switch (st) {
                    .idle => seen = 1,
                    .busy => |b| seen += b,
                }
                st = .idle;
                if (st != .busy) seen += 2;
                return @intCast((acc + bonus + seen + resets) % 256);
            }
            """);
        // Task #109: a union switch takes an assignment prong (`.reset => acc.* = 1`) and its capture twin
        // (`.add => |n| acc.* += n`); `c == .reset` / `.add == c` / `st != .busy` compare the union's TAG; and a capture
        // switch after `+=` fills a temp, as after `=`. zig returns 193.
        cs.ShouldContain("*acc += n;");
        cs.ShouldContain("*acc *= n__1;");
        cs.ShouldContain("(int)c.__tag == (int)Cmd_Tag.reset");
        cs.ShouldContain("(int)st.__tag != (int)__AnonUnion0_Tag.busy");
        cs.ShouldContain("bonus += __vcf0;");
        cs.ShouldContain("seen += b;");
    }

    [Fact]
    public void Inline_container_types_are_call_arguments_too()
    {
        var cs = EmitZig("""
            fn area(s: anytype) u32 {
                return @as(u32, s.w) * s.h;
            }

            pub fn main() u8 {
                const v = @as(struct { a: u8, b: u8 = 2 }, .{ .a = 4 });
                const e = @as(enum { x, y, z }, .z);
                const u = @as(union(enum) { small: u8, big: u16 }, .{ .big = 30 });
                const T = @TypeOf(@as(union { p: u8, q: u16 }, .{ .p = 1 }));
                const t: T = .{ .p = 9 };
                const big: u16 = switch (u) {
                    .small => |s| s,
                    .big => |b| b,
                };
                const r = area(@as(struct { w: u8, h: u32 }, .{ .w = 3, .h = 5 }));
                return @intCast(v.a + v.b + @intFromEnum(e) + big + t.p + r);
            }
            """);
        // Task #110: `struct { … }` / `enum { … }` / `union(enum) { … }` / `union { … }` as a call ARGUMENT (`@as`, `@TypeOf`'s
        // operand, an `anytype` parameter), reified per occurrence as in an annotation (#104). zig returns 62.
        cs.ShouldContain("__AnonStruct0 v = (__AnonStruct0)new __AnonStruct0 { a = 4, b = 2 };");
        cs.ShouldContain("__AnonEnum1 e = (__AnonEnum1)__AnonEnum1.z;");
        cs.ShouldContain("__AnonUnion2 u = (__AnonUnion2)new __AnonUnion2 { __tag = __AnonUnion2_Tag.big");
        cs.ShouldContain("__AnonUnion3 t = new __AnonUnion3 { p = 9 };");
        cs.ShouldContain("uint r = area____AnonStruct4((__AnonStruct4)new __AnonStruct4 { w = 3, h = 5 });");
    }

    [Fact]
    public void In_function_enum_and_union_declarations_register_under_the_function()
    {
        var cs = EmitZig("""
            fn score() u16 {
                const Suit = enum(u8) { clubs = 1, hearts = 3, spades = 7 };
                const Card = union(enum) { pip: u8, face: Suit, joker };
                const Raw = union { word: u16, half: u8 };
                const hand = [_]Card{ .{ .pip = 9 }, .{ .face = .hearts }, .joker, .{ .face = .spades } };
                var total: u16 = 0;
                for (hand) |c| {
                    total += switch (c) {
                        .pip => |p| p,
                        .face => |s| @as(u16, @intFromEnum(s)) * 10,
                        .joker => 50,
                    };
                }
                const r = Raw{ .word = 5 };
                return total + r.word;
            }

            fn other() u8 {
                const Suit = enum { a, b, c };
                return @intFromEnum(Suit.c);
            }

            pub fn main() u8 {
                return @intCast((score() + other()) % 256);
            }
            """);
        // Task #111: an in-function `const E = enum(u8) { … };` / `union(enum)` / `union` registers under `<fn>__<Name>`,
        // like a local struct (W2), so two functions' `Suit`s never collide. `(score() + other()) % 256` over a `u16` and a
        // `u8` call is unsigned `%` (a plain function's declared return type is the zig peer type). zig returns 166.
        cs.ShouldContain("new score__Card { __tag = score__Card_Tag.face, __payload = new score__Card_Payload { face = score__Suit.hearts } }");
        cs.ShouldContain("score__Raw r = new score__Raw { word = 5 };");
        cs.ShouldContain("return (byte)((int)other__Suit.c);");
        cs.ShouldContain("return (byte)((score() + other()) % 256);");
    }

    [Fact]
    public void A_signed_call_result_still_needs_an_explicit_remainder()
    {
        // Task #111: the call's declared return type is the peer, so an `i16` result is still signed `%`, as zig says.
        Should.Throw<CompileException>(() => EmitZig("""
            fn neg() i16 {
                return -7;
            }
            pub fn main() u8 {
                return @intCast(@mod(neg(), 5) + (neg() % 5));
            }
            """)).Message.ShouldContain("signed integers and floats must use @rem or @mod");
    }

    [Fact]
    public void A_container_type_is_a_switch_prong_value()
    {
        var cs = EmitZig("""
            fn Pick(comptime T: type) type {
                return switch (@typeInfo(T)) {
                    .int => struct { lo: T, hi: T },
                    .@"union" => |u| struct {
                        pub const layout = u.layout;
                        tag: u8,
                    },
                    .@"enum" => enum { first, second },
                    else => T,
                };
            }

            pub fn main() u8 {
                const p: Pick(u16) = .{ .lo = 3, .hi = 40 };
                const q: Pick(bool) = true;
                const e: Pick(enum { a }) = .second;
                return @intCast(p.lo + p.hi + @intFromBool(q) + @intFromEnum(e));
            }
            """);
        // Task #108 (std.MultiArrayList's `Elem = switch (@typeInfo(T)) { .@"struct" => T, .@"union" => |u| struct { … } }`):
        // a `struct { … }` / `enum { … }` as a prong's value, plain or with a capture. The selected prong is reified; the
        // others only parse. zig returns 45.
        cs.ShouldContain("__AnonStruct0 p = new __AnonStruct0 { lo = 3, hi = 40 };");
        cs.ShouldContain("__AnonEnum2 e = __AnonEnum2.second;");
    }

    [Fact]
    public void A_multi_object_for_takes_any_objects_and_captures()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                const src = [_]u8{ 1, 2, 3, 4 };
                var dst: [4]u8 = undefined;
                const scale = [_]u8{ 10, 20, 30, 40 };
                for (src, &dst, scale) |s, *d, k| {
                    d.* = s + k;
                }
                var weighted: u32 = 0;
                for (dst, scale, 0..) |d, k, i| {
                    weighted += @as(u32, d) * @as(u32, @intCast(i)) + k;
                }
                var a: [3]u8 = .{ 0, 0, 0 };
                var b: [3]u8 = .{ 0, 0, 0 };
                var c: [3]u8 = .{ 0, 0, 0 };
                const order = [_]u8{ 2, 0, 1 };
                for (order, &a, &b, &c) |o, *x, *y, *z| {
                    x.* = o;
                    y.* = o * 2;
                    z.* = o * 3;
                }
                var ranged: u32 = 0;
                for (5..8, order) |r, o| {
                    ranged += @as(u32, @intCast(r)) * o;
                }
                var trailing: u32 = 0;
                for (
                    src,
                    scale,
                ) |s, k| {
                    trailing += s * k;
                }
                const sum = weighted + a[0] + b[1] + c[2] + ranged + trailing;
                return @intCast(sum % 256);
            }
            """);
        // Task #108 (std.MultiArrayList): one production for the multi-object `for` in place of nine fixed shapes. A `*`
        // capture at any position (`|s, *d, k|`), a `0..` object anywhere (`(dst, scale, 0..)`), four objects with three
        // pointer captures, a bounded range object (`5..8`), and zig fmt's trailing comma. zig returns 130.
        cs.ShouldContain("byte* d = &__s__1.Ptr[__i];");
        cs.ShouldContain("ulong i = __i__1;");
        cs.ShouldContain("byte* x = &__s__6.Ptr[__i__2];");
        cs.ShouldContain("ulong r = (ulong)(5) + __i__3;");
    }

    [Theory]
    [InlineData("var a = [_]u8{ 1, 2 };\n    var t: u8 = 0;\n    for (a, 0..) |x| t += x;\n    return t;", "needs 2 captures; it has 1")]
    [InlineData("var a = [_]u8{ 1, 2 };\n    var t: u8 = 0;\n    for (a, 0..) |x, *i| t += x + @as(u8, @intCast(i.*));\n    return t;", "cannot be taken by reference")]
    public void A_multi_object_for_rejects_mismatched_or_by_reference_index_captures(string body, string message)
    {
        // Task #108: the objects and captures pair up one to one, and a range's index is a value, not storage.
        Should.Throw<CompileException>(() => EmitZig("pub fn main() u8 {\n    " + body + "\n}\n")).Message.ShouldContain(message);
    }

    [Fact]
    public void A_nested_container_of_a_generic_instance_sees_the_instance_value_consts()
    {
        var cs = EmitZig("""
            fn Columns(comptime T: type) type {
                return struct {
                    rows: u8 = 0,
                    const Self = @This();
                    const width = @sizeOf(T);
                    const names = [_][]const u8{ "lo", "mid", "hi" };
                    pub const Slice = struct {
                        ptrs: [names.len]u16,
                        bytes: [width]u8,
                        pub fn total(s: Slice) u32 {
                            var t: u32 = 0;
                            for (s.ptrs) |p| t += p;
                            return t + @as(u32, @intCast(s.bytes.len));
                        }
                    };
                    fn slice(self: Self) Slice {
                        var s: Slice = undefined;
                        for (&s.ptrs, 0..) |*p, i| p.* = @intCast(i * 10 + self.rows);
                        for (&s.bytes) |*b| b.* = 0;
                        return s;
                    }
                };
            }

            pub fn main() u8 {
                const c: Columns(u32) = .{ .rows = 4 };
                const s = c.slice();
                return @intCast(s.total());
            }
            """);
        // Task #108 (std.MultiArrayList's `Slice` has `ptrs: [field_names.len][*]u8` over the instance's
        // `const field_names = …`): the instance's value consts register before its nested containers' bodies, so a nested
        // field's array extent can name one. zig returns 46.
        cs.ShouldContain("public fixed ushort ptrs[3];");
        cs.ShouldContain("public fixed byte bytes[4];");
    }

    [Fact]
    public void A_for_else_statement_runs_the_else_only_when_the_loop_ends_without_a_break()
    {
        var cs = EmitZig("""
            fn find(xs: []const u8, want: u8) u8 {
                for (xs, 0..) |x, i| {
                    if (x == want) break;
                    _ = i;
                } else {
                    return 100;
                }
                return 1;
            }

            fn firstGap(xs: []const u8) u8 {
                for (xs, 0..) |x, i| {
                    if (x != i) return @intCast(i);
                } else {
                    return 50;
                }
            }

            pub fn main() u8 {
                const xs = [_]u8{ 0, 1, 2, 7 };
                var total: u32 = 0;
                total += find(&xs, 2);
                total += find(&xs, 9);
                total += firstGap(&xs);
                total += firstGap(xs[0..3]);
                var outer: u32 = 0;
                while (outer < 5) : (outer += 1) {
                    for (xs) |x| {
                        if (x == 99) break;
                    } else {
                        if (outer == 2) break;
                    }
                }
                total += outer;
                var hits: u32 = 0;
                for (xs) |x| {
                    if (x > 5) continue;
                    hits += 1;
                } else hits += 10;
                return @intCast(total + hits);
            }
            """);
        // Task #108 (std.meta.FieldEnum's `for (values, 0..) |v, i| { … break; } else { return EnumTag; }`): the loop's own
        // exit sets a flag that guards the else after the loop, so a `break` skips it and a `break` inside the else still
        // reaches the OUTER loop; with no break in the body the else follows unguarded (C# then sees it return). zig
        // returns 169.
        cs.ShouldContain("CBool __natural = false;");
        cs.ShouldContain("__natural = true;");
        cs.ShouldContain("if (Cond.B(__natural))");
    }

    [Fact]
    public void A_capture_prong_with_a_capture_if_body_parses_while_another_prong_is_selected()
    {
        // Task #108 (std.meta.FieldEnum's `.@"union" => |u| if (u.tag_type) |E| { … }`): a struct subject selects `else`,
        // so the union prong only has to parse.
        var cs = EmitZig("""
            fn Width(comptime T: type) u8 {
                switch (@typeInfo(T)) {
                    .@"union" => |u| if (u.tag_type) |E| {
                        return @sizeOf(E);
                    },
                    else => {},
                }
                return @sizeOf(T);
            }
            const P = struct { a: u32, b: u16 };
            pub fn main() u8 {
                return Width(P);
            }
            """);
        cs.ShouldContain("Width");
    }

    [Fact]
    public void Enum_reifies_an_enum_as_a_type_returning_function_result()
    {
        var cs = EmitZig("""
            fn Flags(comptime width: u8) type {
                return @Enum(u8, .exhaustive, &.{ "read", "write", "exec" }, &.{ 1, 2, width });
            }

            fn Level(comptime n: usize) type {
                return @Enum(u4, .nonexhaustive, &.{ "low", "high" }, &.{ 0, n -| 1 });
            }

            fn describe(f: Flags(4)) u8 {
                return switch (f) {
                    .read => 10,
                    .write => 20,
                    .exec => 40,
                };
            }

            pub fn main() u8 {
                const F = Flags(4);
                const x: F = .exec;
                const L = Level(9);
                const h: L = .high;
                const sat: u8 = @as(u8, 3) -| 5;
                const big: u8 = @as(u8, 250) +| 10;
                return @intFromEnum(x) + describe(.write) + @as(u8, @intFromEnum(h)) + sat + (big - 250);
            }
            """);
        // Task #108 (std.meta.FieldEnum's `return @Enum(IntTag, .exhaustive, field_names, &values)`): a type-returning body
        // that returns `@Enum(TagInt, mode, names, &values)` registers the enum under the instance's name, members and tag
        // type as spelled. A saturating op over comptime operands folds (FieldEnum's `field_names.len -| 1`), clamped to the
        // peer type. zig returns 37.
        cs.ShouldContain("enum Flags__4 : byte");
        cs.ShouldContain("exec = 4,");
        cs.ShouldContain("high = 8,");
        cs.ShouldContain("byte sat = (byte)0;");
        cs.ShouldContain("byte big = (byte)255;");
    }

    [Theory]
    [InlineData("&.{ \"a\", \"a\" }, &.{ 0, 1 }", "duplicate enum field name 'a'")]
    [InlineData("&.{ \"a\", \"b\" }, &.{ 1, 1 }", "enum tag value 1 already taken")]
    [InlineData("&.{ \"a\", \"b\" }, &.{ 1 }", "1 field value(s) for 2 field name(s)")]
    public void Enum_rejects_duplicate_or_mismatched_members(string lists, string message)
    {
        // Task #108: the names and values of an `@Enum` pair up one to one and are each unique, as zig checks.
        Should.Throw<CompileException>(() => EmitZig(
            "fn E() type {\n    return @Enum(u8, .exhaustive, " + lists + ");\n}\npub fn main() u8 {\n    const x: E() = .a;\n    return @intFromEnum(x);\n}\n"))
            .Message.ShouldContain(message);
    }

    [Theory]
    [InlineData("const s: u8 = -2; return s;", "type 'u8' cannot represent integer value '-2'")]
    [InlineData("const s: u8 = 3 -| 5; return s;", "type 'u8' cannot represent integer value '-2'")]
    [InlineData("const s: u8 = 300; return s;", "type 'u8' cannot represent integer value '300'")]
    [InlineData("var s: u3 = 8; _ = &s; return s;", "type 'u3' cannot represent integer value '8'")]
    [InlineData("const s: i16 = -40000 + 1; return @truncate(@as(u16, @bitCast(s)));", "type 'i16' cannot represent integer value '-39999'")]
    public void A_typed_declaration_rejects_a_comptime_value_its_type_cannot_hold(string body, string message)
    {
        // Task #112: an untyped comptime integer initializer is checked against the declared width, before any narrowing,
        // with zig's own message (`3 -| 5` is comptime_int arithmetic, so -2). dotcc had narrowed or wrapped it silently.
        Should.Throw<CompileException>(() => EmitZig("pub fn main() u8 {\n    " + body + "\n}\n")).Message.ShouldContain(message);
    }

    [Theory]
    [InlineData("const s: i8 = -128; return @bitCast(s);")]
    [InlineData("const s: u8 = 255; return s;")]
    [InlineData("const s: u64 = 0xFFFF_FFFF_FFFF_FFFF; return @truncate(s);")]
    [InlineData("const s: u8 = @as(u8, 3) -| 5; return s;")]
    public void A_typed_declaration_accepts_a_comptime_value_at_the_edge_of_its_type(string body)
    {
        // Task #112: the extremes of a type fit, and a typed saturating op clamps before the check.
        Should.NotThrow(() => EmitZig("pub fn main() u8 {\n    " + body + "\n}\n"));
    }

    [Fact]
    public void A_struct_built_by_a_comptime_block_holds_pinned_pointers_not_stack_ones()
    {
        var cs = EmitZig("""
            const Meta = struct {
                count: u32,
                first: [*]const u32,
            };

            const Table = struct {
                vals: [*]const u32,
                len: u32,
                peak: u32 = 0,
                meta: *const Meta = &empty_meta,

                const empty_vals = [0]u32{};
                const empty_meta = Meta{ .count = 0, .first = &empty_vals };

                inline fn build(comptime n: u32) Table {
                    comptime {
                        var self = Table{ .vals = &empty_vals, .len = n };
                        if (n == 0) return self;
                        var arr: [n]u32 = undefined;
                        for (&arr, 0..) |*e, i| {
                            e.* = @intCast(i * i);
                            self.peak = @max(self.peak, e.*);
                        }
                        const fin = arr;
                        self.vals = &fin;
                        self.meta = &.{ .count = n * 10, .first = &fin };
                        return self;
                    }
                }

                fn at(t: Table, i: u32) u32 {
                    return t.vals[i];
                }
            };

            fn churn(depth: u32) u32 {
                var buf: [64]u32 = undefined;
                for (&buf, 0..) |*b, i| b.* = @intCast(i + depth);
                return if (depth == 0) buf[63] else churn(depth - 1) + buf[0];
            }

            pub fn main() u8 {
                const t = Table.build(5);
                const e = Table.build(0);
                // Deep calls overwrite the stack a dangling pointer into build's frame would still read.
                const noise = churn(8);
                return @intCast((t.at(3) + t.len + t.peak + e.len + t.meta.count + t.meta.first[4] + e.meta.count + noise) % 256);
            }
            """);
        // Task #100, step 1 (SILENT MISCOMPILE): `inline fn build(comptime n) Table { comptime { …; self.vals = &fin;
        // return self; } }` had lowered as runtime code, so the returned struct pointed into build's dead stack frame. A
        // struct-returning comptime block is now evaluated by the interpreter (an early `return` is its result) and spliced
        // with every pointer field's target pinned for the program's life, `&.{ … }` included. zig returns 195.
        cs.ShouldContain("vals = Libc.GlobalArrayFrom<uint>(new uint[]{ 0u, 1u, 4u, 9u, 16u })");
        cs.ShouldContain("meta = Libc.GlobalArrayFrom<Meta>(new Meta[]{ new Meta { count = 50u");
        cs.ShouldNotContain("stackalloc uint[5]");
    }

    [Fact]
    public void A_comptime_block_runs_statement_by_statement_so_later_ones_fold_earlier_results()
    {
        var cs = EmitZig("""
            const Hist = struct {
                counts: [*]const u8,
                len: u32,
                peak: u32 = 0,
                label_len: u32 = 0,

                inline fn build(comptime n: u32) Hist {
                    comptime {
                        var self = Hist{ .counts = undefined, .len = 0 };
                        for (0..n) |i| self.peak = @max(self.peak, @as(u32, @intCast(i * 3 % 7)));
                        // Sized by what the block computed so far.
                        var bins: [self.peak + 1]u8 = undefined;
                        for (&bins, 0..) |*b, i| b.* = @intCast(i + 1);
                        const fin = bins;
                        self.counts = &fin;
                        self.len = self.peak + 1;
                        const labels = .{ .{ "ab", 1 }, .{ "cde", 2 }, .{ "f", 4 } };
                        for (labels, 0..) |l, i| self.label_len += @as(u32, l.@"0".len) * l.@"1" + @as(u32, @intCast(i));
                        return self;
                    }
                }
            };

            pub fn main() u8 {
                const h = Hist.build(6);
                const pair = .{ "xyz", 7 };
                return @intCast(h.counts[h.len - 1] + h.len + h.peak + h.label_len + pair.@"0".len + pair.@"1");
            }
            """);
        // Task #100, step 2: std.StaticStringMap.initComptime sizes an array by what its comptime block computed so far
        // (`[self.max_len + 1]u32`) and iterates a tuple of key/value pairs. The block is now lowered and interpreted one
        // statement at a time, each statement's locals published as comptime values for the next one's lowering, and a
        // tuple is iterated by unrolling with `kv.@"0"` read by position. zig returns 45.
        cs.ShouldContain("counts = Libc.GlobalArrayFrom<byte>(new byte[]{ (byte)1, (byte)2, (byte)3, (byte)4, (byte)5, (byte)6, (byte)7 }), len = 7u, peak = 6u, label_len = 15u");
    }

    [Fact]
    public void A_runtime_for_over_a_tuple_is_rejected_like_zig()
    {
        // Task #100: a tuple's elements differ in type, so zig iterates one only at comptime (or with `inline for`).
        Should.Throw<Exception>(() => EmitZig("""
            pub fn main() u8 {
                const pairs = .{ .{ "ab", 1 }, .{ "cde", 2 } };
                var t: u32 = 0;
                for (pairs, 0..) |p, i| t += p.@"0".len * p.@"1" + @as(u32, @intCast(i));
                return @intCast(t);
            }
            """)).Message.ShouldContain("tuple field index must be comptime-known");
    }

    [Fact]
    public void A_comptime_anytype_tuple_argument_keys_its_instance_and_is_iterated_at_comptime()
    {
        var cs = EmitZig("""
            const Entry = struct {
                key: []const u8,
                value: u8,
            };

            const Table = struct {
                keys: [*]const []const u8,
                values: [*]const u8,
                len: u32,
                longest: u32 = 0,

                inline fn init(comptime pairs: anytype) Table {
                    comptime {
                        var keys: [pairs.len][]const u8 = undefined;
                        var values: [pairs.len]u8 = undefined;
                        var self = Table{ .keys = undefined, .values = undefined, .len = pairs.len };
                        fill(&self, pairs, &keys, &values);
                        const fin_keys = keys;
                        const fin_values = values;
                        self.keys = &fin_keys;
                        self.values = &fin_values;
                        return self;
                    }
                }

                fn fill(self: *Table, pairs: anytype, keys: [][]const u8, values: []u8) void {
                    for (pairs, 0..) |kv, i| {
                        keys[i] = kv.@"0";
                        values[i] = kv.@"1";
                        self.longest = @max(self.longest, @as(u32, @intCast(kv.@"0".len)));
                    }
                }

                fn find(t: Table, key: []const u8) ?Entry {
                    var i: u32 = 0;
                    while (i < t.len) : (i += 1) {
                        const k = t.keys[i];
                        if (k.len != key.len) continue;
                        var j: usize = 0;
                        while (j < k.len and k[j] == key[j]) : (j += 1) {}
                        if (j == k.len) return .{ .key = k, .value = t.values[i] };
                    }
                    return null;
                }
            };

            const small = Table.init(.{ .{ "one", 1 }, .{ "three", 3 }, .{ "ten", 10 } });
            const other = Table.init(.{ .{ "seven", 7 }, .{ "forty", 40 } });

            pub fn main() u8 {
                var total: u32 = small.longest * 100 + other.len;
                if (small.find("three")) |e| total += e.value + @as(u32, @intCast(e.key.len));
                if (other.find("forty")) |e| total += e.value;
                if (small.find("four") == null) total += 20;
                return @intCast(total % 256);
            }
            """);
        // Task #100, step 3: std.StaticStringMap.initComptime's `comptime kvs_list: anytype` fed a tuple of pairs. The tuple's
        // comptime value keys the instance by digest (two tables, two instances) and the body reads it as a comptime aggregate;
        // the helper iterating it is called only from the comptime block, so zig accepts it. `return .{ … }` at a `?Entry`
        // result is the optional's payload. zig returns 58.
        cs.ShouldContain("values = Libc.GlobalArrayFrom<byte>(new byte[]{ (byte)1, (byte)3, (byte)10 }), len = 3u, longest = 5u");
        cs.ShouldContain("public static unsafe Table small = Table_init__ct");
        cs.ShouldContain("public static unsafe Table other = Table_init__ct");
        cs.ShouldContain("return new Entry { key = k, value = t.values[i] };");
    }

    [Fact]
    public void A_function_iterating_a_tuple_called_at_runtime_is_rejected_like_zig()
    {
        // Task #100: accepted while only the comptime block calls it; a runtime call reaching it is zig's error.
        Should.Throw<Exception>(() => EmitZig("""
            const Entry = struct {
                key: []const u8,
                value: u8,
            };

            const Table = struct {
                keys: [*]const []const u8,
                values: [*]const u8,
                len: u32,
                longest: u32 = 0,

                inline fn init(comptime pairs: anytype) Table {
                    comptime {
                        var keys: [pairs.len][]const u8 = undefined;
                        var values: [pairs.len]u8 = undefined;
                        var self = Table{ .keys = undefined, .values = undefined, .len = pairs.len };
                        fill(&self, pairs, &keys, &values);
                        const fin_keys = keys;
                        const fin_values = values;
                        self.keys = &fin_keys;
                        self.values = &fin_values;
                        return self;
                    }
                }

                fn fill(self: *Table, pairs: anytype, keys: [][]const u8, values: []u8) void {
                    for (pairs, 0..) |kv, i| {
                        keys[i] = kv.@"0";
                        values[i] = kv.@"1";
                        self.longest = @max(self.longest, @as(u32, @intCast(kv.@"0".len)));
                    }
                }

                fn find(t: Table, key: []const u8) ?Entry {
                    var i: u32 = 0;
                    while (i < t.len) : (i += 1) {
                        const k = t.keys[i];
                        if (k.len != key.len) continue;
                        var j: usize = 0;
                        while (j < k.len and k[j] == key[j]) : (j += 1) {}
                        if (j == k.len) return .{ .key = k, .value = t.values[i] };
                    }
                    return null;
                }
            };

            const small = Table.init(.{ .{ "one", 1 }, .{ "three", 3 }, .{ "ten", 10 } });
            const other = Table.init(.{ .{ "seven", 7 }, .{ "forty", 40 } });

            pub fn main() u8 {
                var total: u32 = small.longest * 100 + other.len;
                if (small.find("three")) |e| total += e.value + @as(u32, @intCast(e.key.len));
                if (other.find("forty")) |e| total += e.value;
                if (small.find("four") == null) total += 20;
                var t = small;
                var ks: [1][]const u8 = undefined;
                var vs: [1]u8 = undefined;
                Table.fill(&t, .{.{ "z", 9 }}, &ks, &vs);
                return @intCast((total + t.longest) % 256);
            }
            """)).Message.ShouldContain("tuple field index must be comptime-known");
    }

    [Fact]
    public void A_global_array_literal_whose_address_is_taken_is_pinned()
    {
        var cs = EmitZig("""
            const P = struct { vals: []const u8 };
            const nums = P{ .vals = &[_]u8{ 7, 9 } };
            const direct: []const u8 = &[_]u8{ 1, 2, 3 };
            pub fn main() u8 {
                return nums.vals[1] + direct[2];
            }
            """);
        // Task #115: a static field's initializer cannot hold a `stackalloc` (CS1503), and the array outlives every frame.
        cs.ShouldContain("vals = new ConstSlice<byte>(Libc.GlobalArrayFrom<byte>(new byte[]{ 7, 9 }), 2UL)");
        cs.ShouldContain("direct = new ConstSlice<byte>(Libc.GlobalArrayFrom<byte>(new byte[]{ 1, 2, 3 }), 3UL)");
    }

    [Fact]
    public void Void_as_data_is_the_runtime_unit()
    {
        var cs = EmitZig("""
            fn Map(comptime V: type) type {
                return struct {
                    keys: []const u8,
                    vals: []const V,

                    const Self = @This();

                    fn get(self: Self, k: u8) ?V {
                        for (self.keys, 0..) |key, i| {
                            if (key == k) return self.vals[i];
                        }
                        return null;
                    }

                    fn has(self: Self, k: u8) bool {
                        return self.get(k) != null;
                    }
                };
            }

            const unit_vals = [3]void{ {}, {}, {} };
            const set = Map(void){ .keys = "abc", .vals = &unit_vals };
            const nums = Map(u8){ .keys = "xy", .vals = &[_]u8{ 7, 9 } };

            pub fn main() u8 {
                var slots: [4]void = undefined;
                slots[2] = {};
                const view: []const void = &slots;
                var many: [*]const void = &unit_vals;
                many += 1;
                _ = many[0];
                var total: u32 = @intCast(view.len + unit_vals.len);
                if (set.has('b')) total += 10;
                if (!set.has('z')) total += 20;
                if (set.get('c')) |_| total += 40;
                if (nums.get('y')) |n| total += n;
                return @intCast(total);
            }
            """);
        // Task #114: std.StaticStringMap(void) stores `[*]const V` values, returns `?V`, swaps `*V`. C# has no void element,
        // generic argument or storage, so zig's void as DATA (the element of a slice, many-pointer, array, optional or tuple,
        // and a `*T` with T = void) is the runtime's empty `Unit`, and the void value `{}` stored there is `default(Unit)`.
        // `{}` parses as a list element too. zig returns 86. A store of the zero-size element is a discard (task #135: zig
        // stores nothing, and a pointer it leaves `undefined` for one must not be written through).
        cs.ShouldContain("Unit* slots = stackalloc Unit[4];");
        cs.ShouldContain("_ = default(Unit);");
        cs.ShouldContain("ConstSlice<Unit> view = new ConstSlice<Unit>(slots, 4UL);");
        cs.ShouldContain("Unit? Map__void_get(Map__void self, byte k)");
        cs.ShouldContain("unit_vals = Libc.GlobalArrayFrom<Unit>(new Unit[]{ default(Unit), default(Unit), default(Unit) });");
    }

    [Fact]
    public void A_pointer_to_anyopaque_stays_an_opaque_void_pointer()
    {
        var cs = EmitZig("""
            fn touch(ctx: *anyopaque) u8 {
                const p: *u8 = @ptrCast(ctx);
                return p.*;
            }
            pub fn main() u8 {
                var x: u8 = 5;
                return touch(&x);
            }
            """);
        // Task #114: only `void` as data becomes `Unit`; `*anyopaque` is still C's `void*`.
        cs.ShouldContain("touch(void* ctx)");
    }

    [Fact]
    public void An_enum_literal_without_a_result_type_coerces_where_it_meets_an_enum()
    {
        var cs = EmitZig("""
            const Color = enum(u8) { red = 1, green = 2, blue = 4 };
            const Shape = union(enum) { none, circle: u8 };

            const Table = struct {
                keys: [*]const u8,
                colors: [*]const Color,
                len: u32,

                inline fn init(comptime pairs: anytype) Table {
                    comptime {
                        var keys: [pairs.len]u8 = undefined;
                        var colors: [pairs.len]Color = undefined;
                        fill(pairs, &keys, &colors);
                        const fin_keys = keys;
                        const fin_colors = colors;
                        return .{ .keys = &fin_keys, .colors = &fin_colors, .len = pairs.len };
                    }
                }

                fn fill(pairs: anytype, keys: []u8, colors: []Color) void {
                    for (pairs, 0..) |kv, i| {
                        keys[i] = kv.@"0";
                        colors[i] = kv.@"1";
                    }
                }

                fn find(t: Table, key: u8) ?Color {
                    var i: u32 = 0;
                    while (i < t.len) : (i += 1) {
                        if (t.keys[i] == key) return t.colors[i];
                    }
                    return null;
                }
            };

            const warm = Table.init(.{ .{ 'r', .red }, .{ 'g', .green } });
            const cool = Table.init(.{ .{ 'r', .blue }, .{ 'g', .green } });

            pub fn main() u8 {
                const lit = .blue;
                const c: Color = lit;
                const maybe: ?Color = lit;
                const none = .none;
                const s: Shape = none;
                var total: u8 = @intFromEnum(c) + @intFromEnum(maybe.?);
                if (s == .none) total += 10;
                if (warm.find('r')) |w| total += @intFromEnum(w) * 16;
                if (cool.find('r')) |w| total += @intFromEnum(w) * 32;
                if (warm.find('x') == null) total += 1;
                return total;
            }
            """);
        // Task #113: std.StaticStringMap(Kw).initComptime(.{ .{ "if", .kw_if }, … }). zig types a bare `.member` as the
        // comptime-only `@EnumLiteral()`; dotcc makes each literal a singleton type carrying its name, so it coerces
        // statically wherever it meets an enum, an optional enum or a tagged union. A `const` of one emits nothing, the
        // tuples key distinct instances, and the helper whose parameter carries the literal type runs only at comptime,
        // so its runtime copy is dropped. zig returns 163.
        cs.ShouldContain("Color c = Color.blue;");
        cs.ShouldContain("Color? maybe = Color.blue;");
        cs.ShouldContain("Shape s = new Shape { __tag = Shape_Tag.none };");
        cs.ShouldContain("colors = Libc.GlobalArrayFrom<Color>(new Color[]{ (Color)(byte)1, (Color)(byte)2 })");
        cs.ShouldNotContain("Table_fill");
    }

    [Fact]
    public void A_var_of_an_enum_literal_is_rejected_like_zig()
    {
        Should.Throw<Exception>(() => EmitZig("""
            const Color = enum { red, blue };
            pub fn main() u8 {
                var lit = .blue;
                _ = &lit;
                const c: Color = lit;
                return @intFromEnum(c);
            }
            """)).Message.ShouldContain("variable of type '@EnumLiteral()' must be const or comptime");
    }

    [Fact]
    public void A_comptime_call_caught_with_unreachable_folds_to_its_payload()
    {
        var cs = EmitZig("""
            fn divCeil(comptime T: type, a: T, b: T) !T {
                if (b == 0) return error.DivisionByZero;
                return (a + b - 1) / b;
            }

            fn widen(comptime T: type, x: T) u64 {
                const bits = @typeInfo(T).int.bits;
                const ceil_bytes = comptime divCeil(u16, bits, 8) catch unreachable;
                const Wide = @Int(.unsigned, ceil_bytes * 8);
                const w: Wide = x;
                return @as(u64, w) + ceil_bytes;
            }

            pub fn main() u8 {
                return @intCast(widen(u12, 100) + widen(u3, 5));
            }
            """);
        // Task #117: std.Random.int's `const ceil_bytes = comptime std.math.divCeil(u16, bits, 8) catch unreachable;` sizes
        // `@Int(.unsigned, ceil_bytes * 8)`. zig's `comptime` takes the whole expression; dotcc's binds tighter, but its fold
        // already evaluated the call, so the `catch` of a success is the payload (at the payload's type), and a comptime
        // position inlines such a const through arithmetic. zig returns 108.
        cs.ShouldContain("ushort ceil_bytes = (ushort)2;");
        cs.ShouldContain("ushort w = x;");
    }

    [Fact]
    public void Slice_fields_read_through_a_single_pointer_to_a_slice()
    {
        var cs = EmitZig("""
            const Entry = struct { key_ptr: *const []const u8, weight: u8 };

            fn score(e: Entry) usize {
                return e.key_ptr.len * e.weight + e.key_ptr.ptr[0];
            }

            pub fn main() u8 {
                const word: []const u8 = "hey";
                var other: []const u8 = "ab";
                const p = &other;
                p.len = 1;
                return @intCast(score(.{ .key_ptr = &word, .weight = 3 }) + other.len + p.ptr[0]);
            }
            """);
        // Task #118: zig auto-dereferences a single pointer for field access, so `e.key_ptr.len` with `key_ptr: *[]const u8`
        // (std.StringHashMap's iterator entries) is the slice's length, and `p.len = 1` writes it. zig returns 211.
        cs.ShouldContain("return e.key_ptr->Len * e.weight + e.key_ptr->Ptr[0];");
        cs.ShouldContain("p->Len = 1;");
    }

    [Fact]
    public void A_comptime_block_loops_over_field_names_and_hands_on_a_slice_of_tuples()
    {
        var cs = EmitZig("""
            const Op = enum(u8) { add = 3, sub = 5, mul = 7 };

            fn Lookup(comptime T: type) type {
                return struct {
                    names: [*]const []const u8,
                    values: [*]const T,
                    len: usize,

                    const Self = @This();

                    inline fn init(comptime pairs: anytype) Self {
                        comptime {
                            var names: [pairs.len][]const u8 = undefined;
                            var values: [pairs.len]T = undefined;
                            fill(pairs, &names, &values);
                            const fin_names = names;
                            const fin_values = values;
                            return .{ .names = &fin_names, .values = &fin_values, .len = pairs.len };
                        }
                    }

                    fn fill(pairs: anytype, names: [][]const u8, values: []T) void {
                        for (pairs, 0..) |kv, i| {
                            names[i] = kv.@"0";
                            values[i] = kv.@"1";
                        }
                    }

                    fn get(self: Self, str: []const u8) ?T {
                        var i: usize = 0;
                        while (i < self.len) : (i += 1) {
                            const n = self.names[i];
                            if (n.len != str.len) continue;
                            var j: usize = 0;
                            while (j < n.len and n[j] == str[j]) : (j += 1) {}
                            if (j == n.len) return self.values[i];
                        }
                        return null;
                    }
                };
            }

            fn toEnum(comptime T: type, str: []const u8) ?T {
                const kvs = comptime build_kvs: {
                    const KV = struct { []const u8, T };
                    var kvs_array: [@typeInfo(T).@"enum".field_names.len]KV = undefined;
                    for (@typeInfo(T).@"enum".field_names, 0..) |name, i| {
                        kvs_array[i] = .{ name, @field(T, name) };
                    }
                    break :build_kvs kvs_array[0..];
                };
                const map = Lookup(T).init(kvs);
                return map.get(str);
            }

            pub fn main() u8 {
                const a = toEnum(Op, "sub") orelse return 99;
                const b = toEnum(Op, "mul") orelse return 98;
                const c = toEnum(Op, "div");
                return @intFromEnum(a) * 10 + @intFromEnum(b) + @as(u8, if (c == null) 100 else 0);
            }
            """);
        // Task #116, std.meta.stringToEnum's shape: in a `comptime build_kvs: { … }` block a plain `for` over
        // `@typeInfo(T).@"enum".field_names` runs at compile time, so it unrolls with comptime captures; `@field(T, name)` of
        // an enum TYPE is the member; the block's `[n]struct { []const u8, T }` zeroes and splices each element at its own
        // type; and the const bound to the block is a comptime slice a `comptime pairs: anytype` parameter is keyed by.
        // zig returns 157.
        cs.ShouldContain("values = Libc.GlobalArrayFrom<Op>(new Op[]{ (Op)(byte)3, (Op)(byte)5, (Op)(byte)7 }), len = 3UL };");
        cs.ShouldContain("3UL), (Op)(byte)3), new System.ValueTuple<ConstSlice<byte>, Op>(");
    }

    [Fact]
    public void Format_guards_over_a_comptime_string_settle_at_compile_time()
    {
        var cs = EmitZig("""
            const ANY = "any";

            fn eql(a: []const u8, b: []const u8) bool {
                if (a.len != b.len) return false;
                for (a, b) |x, y| if (x != y) return false;
                return true;
            }

            fn check(comptime fmt: []const u8) u8 {
                switch (fmt.len) {
                    3 => if (fmt[0] == 'b' and fmt[1] == '6' and fmt[2] == '4') switch (fmt[0]) {
                        'b' => return 1,
                        else => @compileError("not b64: " ++ fmt),
                    },
                    else => {},
                }
                const is_any = comptime eql(fmt, ANY);
                if (!is_any and fmt.len > 1) @compileError("bad format " ++ fmt);
                return @as(u8, @intFromBool(is_any)) * 5 + 2;
            }

            fn bitsOf(v: anytype) u16 {
                return @typeInfo(@TypeOf(v)).int.bits;
            }

            const Rec = struct { wide: u21, narrow: u8 };
            const Pair = struct { u8, u16 };

            pub fn main() u8 {
                const r = Rec{ .wide = 3, .narrow = 4 };
                const tuple_flags: u8 = (if (@typeInfo(Rec).@"struct".is_tuple) 100 else 0) + (if (@typeInfo(Pair).@"struct".is_tuple) 10 else 0);
                const bits: u16 = bitsOf(@field(r, "wide")) + bitsOf(r.narrow);
                return check("any") + check("b64") * 20 + check("x") + tuple_flags + @as(u8, @intCast(bits));
            }
            """);
        // Task #121, std.Io.Writer.printValue's `{any}` guards: `fmt[0] == 'b' and ...` over a comptime string, a local
        // `const is_any = comptime eql(fmt, ANY)` (a top-level string const, curated std.mem.eql in the interpreter) and
        // `!is_any and ...` all settle, so the guarded `@compileError` arms are never analysed; `@intFromBool` of the settled
        // bool is its 0 or 1; `is_tuple` folds; a struct field read carries its declared width (`u21`). zig returns 68.
        cs.ShouldContain("return (byte)((byte)1 * 5 + 2);");
        cs.ShouldContain("bitsOf__u32w21(r.wide)");
    }

    [Fact]
    public void A_pointer_size_class_rides_the_pointer_spelling()
    {
        var cs = EmitZig("""
            const S = struct { a: u8 };

            fn classify(p: anytype) u8 {
                const P = @TypeOf(p);
                return switch (@typeInfo(P).pointer.size) {
                    .one => 1,
                    .many => 2,
                    .c => 3,
                    .slice => 4,
                };
            }

            fn isOne(p: anytype) u8 {
                return if (@typeInfo(@TypeOf(p)).pointer.size == .one) 10 else 20;
            }

            fn viaMany(q: [*]const u8) u8 {
                return classify(q) + isOne(q);
            }

            fn viaC(q: [*c]const u8) u8 {
                return classify(q);
            }

            pub fn main() u8 {
                var s = S{ .a = 5 };
                const buf = [_]u8{ 7, 8, 9 };
                const sl: []const u8 = &buf;
                const one: *S = &s;
                var total: u8 = 0;
                total += classify(&s);
                total += classify(one) * 3;
                total += classify(sl.ptr) * 5;
                total += viaMany(&buf);
                total += viaC(&buf) * 7;
                total += classify(sl) * 11;
                total += isOne(&s);
                total += s.a;
                return total;
            }
            """);
        // Task #119, std.Random.init's `assert(@typeInfo(Ptr).pointer.size == .one)`: `*T`, `[*]T` and `[*c]T` lower to one C
        // pointer, so the class is read from the spelling (`&x`, a typed local, a parameter, a slice's `.ptr`) and each class
        // keys its own instance of a generic that reads it. zig returns 116.
        cs.ShouldContain("classify__p_S(S* p)");
        cs.ShouldContain("classify__p_u8pm(byte* p)");
        cs.ShouldContain("classify__p_u8pc(byte* p)");
        cs.ShouldContain("isOne__p_S(S* p)");
        cs.ShouldContain("isOne__p_u8pm(byte* p)");
    }

    [Fact]
    public void A_pointer_whose_size_class_no_spelling_gives_is_still_rejected()
    {
        Should.Throw<Exception>(() => EmitZig("""
            var store = [_]u8{ 1, 2 };
            fn get() [*]u8 {
                return &store;
            }
            fn classify(p: anytype) u8 {
                return if (@typeInfo(@TypeOf(p)).pointer.size == .one) 1 else 2;
            }
            pub fn main() u8 {
                return classify(get());
            }
            """)).Message.ShouldContain("pointer SIZE class is not recoverable");
    }

    [Fact]
    public void An_in_function_struct_method_sees_its_own_instance_alias()
    {
        var cs = EmitZig("""
            const A = struct {
                v: u8,
                fn get(self: *A) u8 {
                    return self.v + 1;
                }
            };

            const B = struct {
                v: u8,
                fn get(self: *B) u8 {
                    return self.v * 3;
                }
            };

            fn call(p: anytype) u8 {
                const P = @TypeOf(p);
                const gen = struct {
                    fn run(q: *anyopaque) u8 {
                        const self: P = @ptrCast(@alignCast(q));
                        return self.get();
                    }
                };
                return gen.run(p);
            }

            pub fn main() u8 {
                var a = A{ .v = 10 };
                var b = B{ .v = 10 };
                return call(&a) + call(&b) * 2;
            }
            """);
        // Task #124, std.Random.init's local `gen.fill`: the method body lowers after BOTH instances, so the enclosing body's
        // `const P = @TypeOf(p);` is one of its seeds. Before, both casts named the last instance's `B` (zig 71, dotcc 90).
        cs.ShouldContain("A* self = (A*)q;");
        cs.ShouldContain("B* self = (B*)q;");
        cs.ShouldContain("return A_get(self);");
    }

    [Fact]
    public void An_enum_member_can_be_valued_by_the_root_units_own_function()
    {
        var cs = EmitZig("""
            const Color = enum(u16) {
                red = base() + 1,
                green = maxOf(u8) - 5,
                blue,
                fn weight(self: Color) u16 {
                    return @intFromEnum(self) % 17;
                }
            };

            fn base() u16 {
                return 40;
            }

            fn maxOf(comptime T: type) T {
                return ~@as(T, 0);
            }

            const Box = struct {
                size: u16,
                const default_size = base() * 2;
            };

            pub fn main() u8 {
                const b = Box{ .size = Box.default_size };
                var total: u16 = base();
                total += @intFromEnum(Color.red) + @intFromEnum(Color.blue);
                total += Color.green.weight();
                total += b.size;
                return @truncate(total);
            }
            """);
        // Task #123: the root unit registers containers before pass 1 declares its functions, so `green = maxOf(u8) - 5`
        // declares `maxOf` early; the member's value is comptime and runs in the interpreter. Pass 1 reuses the early
        // declaration, so `base` (also called at runtime) is emitted once. zig returns 168.
        cs.ShouldContain("red = 41,");
        cs.ShouldContain("green = 250,");
        cs.ShouldContain("blue = 251,");
        (cs.Split("ushort @base()").Length - 1).ShouldBe(1);
    }

    [Fact]
    public void Std_mem_bytes_as_value_reads_and_writes_through_the_bytes()
    {
        var cs = EmitZig("""
            const std = @import("std");

            const P = extern struct { a: u16, b: u16 };

            pub fn main() u8 {
                var buf = [_]u8{ 1, 0, 2, 0 };
                const p = std.mem.bytesAsValue(P, &buf);
                p.b = 7;
                const lit = std.mem.bytesToValue(u32, "\x05\x00\x00\x01");
                const sl: []const u8 = buf[0..];
                const q = std.mem.bytesToValue(P, sl[0..4]);
                return @intCast(buf[2] * 10 + q.a + (lit >> 24) + (lit & 0xff));
            }
            """);
        // Task #126: std.mem.bytesAsValue / bytesToValue, curated like asBytes (their return type is reified through
        // `@Pointer`): a pointer cast of the bytes' data pointer, from a pointer to an array, a slice or a string literal;
        // bytesToValue reads through it. zig returns 77.
        cs.ShouldContain("P* p = (P*)buf;");
        cs.ShouldContain("uint lit = *(uint*)");
        cs.ShouldContain("P q = *(P*)");
    }

    [Fact]
    public void Std_mem_bytes_to_value_of_an_array_value_is_rejected()
    {
        // zig: `bytes: anytype` must be a pointer (`@typeInfo(B).pointer`); an array VALUE is a compile error there too.
        Should.Throw<Exception>(() => EmitZig("""
            const std = @import("std");
            pub fn main() u8 {
                const arr = [_]u8{ 1, 0, 2, 0 };
                return @truncate(std.mem.bytesToValue(u32, arr));
            }
            """)).Message.ShouldContain("expects a pointer to bytes or a byte slice");
    }

    [Fact]
    public void A_vector_shape_dotnet_cannot_hold_is_an_array_at_compile_time()
    {
        var cs = EmitZig("""
            inline fn iota(comptime n: usize) @Vector(n, u8) {
                comptime {
                    var out: [n]u8 = undefined;
                    for (&out, 0..) |*e, i| e.* = @intCast(i * 3);
                    return out;
                }
            }

            fn Box(comptime T: type) type {
                return struct {
                    v: T,
                    const Self = @This();
                    fn widen(self: *Self, lanes: @Vector(3, u8)) void {
                        _ = self;
                        _ = lanes;
                    }
                    fn get(self: Self) T {
                        return self.v;
                    }
                };
            }

            const P = struct { a: u8, b: u32 };

            pub fn main() u8 {
                const v = comptime iota(3);
                const arr: [3]u8 = v;
                const b = Box(u8){ .v = 40 };
                var wide: @FieldType(P, "b") = 70000;
                wide += 1;
                return arr[2] + arr[1] + b.get() + @as(u8, @intCast(wide % 7));
            }
            """);
        // Task #108 (std.meta.FieldEnum's `&std.simd.iota(u8, 3)`): a `@Vector(3, u8)` evaluated at compile time is held as a
        // `[3]u8`; the uncalled method taking one by value is not declared at all (its failure waits for a call, as zig
        // analyses it only when referenced); `@FieldType(P, "b")` is the field's type. zig returns 50.
        cs.ShouldContain("uint wide = 70000;");
        cs.ShouldNotContain("_widen(");
    }

    [Fact]
    public void A_vector_shape_dotnet_cannot_hold_is_still_rejected_at_runtime()
    {
        // The runtime array fallback is GitHub issue #127: at runtime the shape stays the loud cut.
        Should.Throw<Exception>(() => EmitZig("""
            inline fn iota(comptime n: usize) @Vector(n, u8) {
                comptime {
                    var out: [n]u8 = undefined;
                    for (&out, 0..) |*e, i| e.* = @intCast(i * 3);
                    return out;
                }
            }
            pub fn main() u8 {
                const v = iota(3);
                const arr: [3]u8 = v;
                return arr[2];
            }
            """)).Message.ShouldContain("dotcc lowers a vector to .NET's Vector64/128/256/512");
        // The same instance made at compile time first, then called at runtime: the call-graph check refuses it.
        Should.Throw<Exception>(() => EmitZig("""
            inline fn iota(comptime n: usize) @Vector(n, u8) {
                comptime {
                    var out: [n]u8 = undefined;
                    for (&out, 0..) |*e, i| e.* = @intCast(i * 3);
                    return out;
                }
            }
            pub fn main() u8 {
                const c = comptime iota(3);
                const v = iota(3);
                const a: [3]u8 = c;
                const b: [3]u8 = v;
                return a[1] + b[2];
            }
            """)).Message.ShouldContain("is called at runtime with a vector shape .NET vectors cannot hold");
    }

    [Fact]
    public void A_reified_method_whose_signature_does_not_lower_fails_when_called()
    {
        Should.Throw<Exception>(() => EmitZig("""
            fn Box(comptime T: type) type {
                return struct {
                    v: T,
                    const Self = @This();
                    fn widen(self: *Self, lanes: @Vector(3, u8)) void {
                        _ = self;
                        _ = lanes;
                    }
                };
            }
            pub fn main() u8 {
                var b = Box(u8){ .v = 40 };
                b.widen(.{ 1, 2, 3 });
                return b.v;
            }
            """)).Message.ShouldContain("dotcc lowers a vector to .NET's Vector64/128/256/512");
    }

    [Fact]
    public void A_nested_container_method_reads_its_enclosing_instance_lists_and_types()
    {
        var cs = EmitZig("""
            fn L(comptime E: type) type {
                return struct {
                    const names = @typeInfo(E).@"struct".field_names;
                    pub const Tag = enum { a, b };
                    pub const In = struct {
                        pub fn count() usize {
                            var n: usize = 0;
                            inline for (names) |_| n += 1;
                            return n;
                        }
                        pub fn pick() Tag {
                            return @field(Tag, "b");
                        }
                    };
                };
            }

            const S = struct { x: u8, y: u8, z: u8 };

            pub fn main() u8 {
                return @intCast(L(S).In.count() * 10 + @intFromEnum(L(S).In.pick()));
            }
            """);
        // Task #108 (std.MultiArrayList.Slice.set / swap): `inline for (names)` over the enclosing instance's
        // `const names = @typeInfo(E).@"struct".field_names;`, and `@field(Tag, "b")` with `Tag` its type const. zig returns 31.
        cs.ShouldContain("L__S__In_count()");
        cs.ShouldContain("L__S__In_pick()");
    }

    [Fact]
    public void A_range_capture_and_an_arithmetic_result_carry_their_declared_width()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                var total: u8 = 0;
                for (0..2) |i| total += @intCast(@typeInfo(@TypeOf(i * i)).int.bits);
                const x: u32 = 7;
                total += @typeInfo(@TypeOf(x + 1)).int.bits;
                const y: u8 = 3;
                total += @typeInfo(@TypeOf(y << 2)).int.bits;
                total += @typeInfo(@TypeOf(y *% y)).int.bits;
                return total;
            }
            """);
        // Task #132 (real std's `{d}` of `i`, `i * i`, `x + 1`: printIntAny asks `@typeInfo(T).int.bits`): a `for (0..n)` capture
        // is a usize; an arithmetic result takes its operands' width (equal widths, or one an integer literal); a shift keeps
        // its left operand's. zig returns 176 (64 + 64 + 32 + 8 + 8).
        cs.ShouldContain("total += (byte)64;");
        cs.ShouldContain("total += (byte)(32);");
        cs.ShouldContain("total += (byte)(8);");
    }

    [Fact]
    public void Arithmetic_over_operands_of_different_widths_keeps_its_width_unknown()
    {
        // zig's peer type of `u32 + u8` is u32, but dotcc does not track signedness here, so it refuses rather than guesses.
        Should.Throw<Exception>(() => EmitZig("""
            pub fn main() u8 {
                const x: u32 = 7;
                const y: u8 = 3;
                return @typeInfo(@TypeOf(x + y)).int.bits;
            }
            """)).Message.ShouldContain("the declared width is not known here");
    }

    [Fact]
    public void A_zig_hex_escape_takes_exactly_two_digits()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                const a = "ab\x00cd";
                const b = "\x41BC\u{e9}a";
                const c = "x\x41";
                var n: u32 = a.len * 10 + a[4] % 10;
                n += b.len * 3 + b[1] + b[3] + b[5] + c.len;
                return @truncate(n);
            }
            """);
        // Task #134, a silent miscompile: the shared (C) decoder's `\x` takes every following hex digit, so zig's
        // `"ab\x00cd"` decoded as `a b 0xCD` (len 3, zig 5). Each zig `\xNN` and `\u{…}` byte now reaches it as an octal
        // escape. zig returns 172.
        cs.ShouldContain("\"ab\\x00\\x63\\x64\\0\"u8");
        Should.Throw<Exception>(() => EmitZig("""
            pub fn main() u8 {
                const s = "\x4";
                return s[0];
            }
            """)).Message.ShouldContain("takes exactly two hex digits");
    }

    [Fact]
    public void Std_mem_slice_to_stops_at_the_end_value()
    {
        var cs = EmitZig("""
            const std = @import("std");
            pub fn main() u8 {
                var buf = [_]u8{ 7, 8, 0, 9 };
                const head = std.mem.sliceTo(&buf, 0);
                head[0] = 1;
                const sl: []const u8 = &[_]u8{ 3, 4, 5 };
                const tail = std.mem.sliceTo(sl, 5);
                const z: [*:0]const u8 = "hey";
                const m = std.mem.sliceTo(z, 0);
                return @intCast(head.len * 100 + buf[0] * 10 + tail.len + m.len * 3);
            }
            """);
        // Task #127: std.mem.sliceTo, curated like asBytes (its return type is reified through `@Pointer`): an array pointer
        // keeps its mutability, a slice is scanned within its length, a many-item pointer up to the `end` it is terminated
        // by. zig returns 221.
        cs.ShouldContain("Slice<byte> head = ZigMem.SliceTo<byte>(");
        cs.ShouldContain("ConstSlice<byte> tail = ZigMem.SliceTo<byte>(sl, 5);");
        cs.ShouldContain("ConstSlice<byte> m = ZigMem.SliceToSentinel<byte>(z, 0);");
    }

    [Fact]
    public void A_value_position_inline_for_unrolls_and_breaks_with_its_value()
    {
        var cs = EmitZig("""
            const E = enum(u8) { alpha, be, gamma };
            fn nameOf(e: E) ?[:0]const u8 {
                const names = @typeInfo(E).@"enum".field_names;
                const values = @typeInfo(E).@"enum".field_values;
                return inline for (names, values) |n, v| {
                    if (@intFromEnum(e) == v) break n;
                } else null;
            }
            pub fn main() u8 {
                return @intCast(nameOf(.alpha).?.len * 10 + nameOf(.gamma).?.len);
            }
            """);
        // Task #131 (std.enums.tagName: `return inline for (field_names, field_values) |f_name, f_value| { … break f_name; }
        // else null;`): the comptime-unrolled value loop; each copy's `break` fills the result and jumps past the `else`,
        // and a name captured from `field_names` meets the `?[:0]const u8` result as a slice. zig returns 55.
        cs.ShouldContain("__lv0_end:");
        cs.ShouldContain("\"alpha\\0\"u8");
        cs.ShouldContain("\"gamma\\0\"u8");
    }

    [Fact]
    public void Align_cast_passes_its_result_type_to_field_parent_ptr()
    {
        var cs = EmitZig("""
            const P = struct { a: u32, b: u8 };

            fn parentOf(b: *u8) *P {
                const p: *P = @alignCast(@fieldParentPtr("b", b));
                return p;
            }

            pub fn main() u8 {
                var x = P{ .a = 5, .b = 7 };
                const q = parentOf(&x.b);
                q.a += 1;
                return @intCast(x.a * 10 + q.b);
            }
            """);
        // Task #129 (std.fmt.count through std.Io.Writer.Discarding: `const d: *Discarding = @alignCast(@fieldParentPtr("writer",
        // w));`): `@alignCast` is the identity in dotcc, so the result type it is given is its operand's, which
        // `@fieldParentPtr` needs to name the parent. zig returns 67.
        cs.ShouldContain("P* p = (P*)((ulong)b - ");
    }

    [Fact]
    public void A_labeled_block_statement_is_left_by_break_to_its_end()
    {
        var cs = EmitZig("""
            fn grow(len: u32, want: u32) u32 {
                var out: u32 = len;
                if (len != want) realloc: {
                    if (want < len) {
                        out = want;
                        break :realloc;
                    }
                    out = want * 2;
                }
                return out;
            }

            fn scan(xs: []const u8) u32 {
                var hits: u32 = 0;
                for (xs) |x| {
                    blk: {
                        if (x == 0) break :blk;
                        if (x > 9) continue;
                        hits += x;
                    }
                    hits += 1;
                }
                return hits;
            }

            pub fn main() u8 {
                const a = grow(4, 4);
                const b = grow(8, 3);
                const c = grow(2, 5);
                const d = scan(&[_]u8{ 1, 0, 20, 3 });
                return @intCast(a + b + c + d);
            }
            """);
        // Task #130 (std.bit_set.DynamicBitSetUnmanaged.resize: `if (…) realloc: { … break :realloc; … }`): a labeled block
        // as a STATEMENT, its `break :lbl;` a goto past the body; an unlabeled `continue` inside still reaches the loop. zig
        // returns 24.
        cs.ShouldContain("goto __blk0_brk;");
        cs.ShouldContain("__blk0_brk:");
    }

    [Fact]
    public void A_while_else_statement_runs_its_else_only_when_the_condition_ends_the_loop()
    {
        var cs = EmitZig("""
            fn firstSet(masks: []const u8) ?usize {
                var offset: usize = 0;
                while (offset < masks.len) {
                    if (masks[offset] != 0) break;
                    offset += 1;
                } else return null;
                return offset;
            }

            fn sumPairs(n: u32) u32 {
                var total: u32 = 0;
                var i: u32 = 0;
                while (i < n) {
                    var j: u32 = 0;
                    while (j < i) {
                        j += 1;
                        if (j == 3) continue;
                        if (j == 5) break;
                        total += j;
                    } else total += 10;
                    i += 1;
                } else total += 100;
                return total;
            }

            pub fn main() u8 {
                const a = firstSet(&[_]u8{ 0, 0, 7, 1 }) orelse 99;
                const b = firstSet(&[_]u8{ 0, 0 }) orelse 9;
                return @intCast(a + b + sumPairs(4));
            }
            """);
        // Task #130 (std.bit_set.DynamicBitSetUnmanaged.findFirstSet: `while (…) { if (…) break; … } else return null;`):
        // the condition exits through a numbered flag, so a `break` skips the else and nested while-else loops do not
        // shadow each other. zig returns 158.
        cs.ShouldContain("__natural");
        cs.ShouldContain("if (Cond.B(__natural");
    }

    [Fact]
    public void A_decl_literal_behind_try_resolves_on_the_result_type()
    {
        var cs = EmitZig("""
            const Inner = struct {
                n: u32,
                pub fn init(n: u32) error{Bad}!Inner {
                    if (n > 100) return error.Bad;
                    return .{ .n = n * 2 };
                }
                pub fn plain(n: u32) Inner {
                    return .{ .n = n + 1 };
                }
            };

            const Outer = struct {
                inner: Inner,
                tag: u8,
                fn make(n: u32) !Outer {
                    return Outer{ .inner = try .init(n), .tag = 3 };
                }
            };

            pub fn main() !u8 {
                const o = try Outer.make(10);
                const p: Outer = .{ .inner = .plain(4), .tag = 1 };
                var q: Inner = try .init(1);
                q = try .init(2);
                const bad: u8 = if (Outer.make(500)) |_| 0 else |_| 7;
                return @intCast(o.inner.n + o.tag + p.inner.n + p.tag + q.n + bad);
            }
            """);
        // Task #130 (std.DynamicBitSet.initEmpty: `.unmanaged = try .initEmpty(allocator, bit_length)`): zig looks the
        // decl literal up through the error union its `try` unwraps. zig returns 40.
        cs.ShouldContain("inner = ErrUnion.Try(Inner_init(n))");
        cs.ShouldContain("Inner q = ErrUnion.Try(Inner_init(1));");
    }

    [Fact]
    public void An_array_container_var_is_a_pinned_global_and_a_comptime_slice_of_it_a_many_pointer()
    {
        var cs = EmitZig("""
            const Set = struct {
                len: usize = 0,
                masks: [*]u32 = empty_masks_ptr,

                var empty_masks_data = [_]u32{ 0, undefined };
                const empty_masks_ptr = empty_masks_data[1..2];

                fn header(self: Set) u32 {
                    return (self.masks - 1)[0];
                }
            };

            var counter: u32 = 5;

            pub fn main() u8 {
                const s: Set = .{};
                counter += 1;
                return @intCast(s.header() + s.len + counter);
            }
            """);
        // Task #130 (std.bit_set: `var empty_masks_data = [_]MaskInt{ 0, undefined };` and `const empty_masks_ptr =
        // empty_masks_data[1..2];` read by a `[*]MaskInt` field default): the var takes the pinned store a top-level
        // array global does, and comptime bounds make zig's `*[1]T`, whose pointer the field takes. zig returns 6.
        cs.ShouldContain("Set_empty_masks_data = Libc.GlobalArrayFrom<uint>");
        cs.ShouldContain("masks = new Slice<uint>(Set_empty_masks_data + 1");
    }

    [Fact]
    public void Catch_unreachable_over_a_void_error_union_is_a_statement()
    {
        var cs = EmitZig("""
            var hits: u8 = 0;

            fn bump(n: u8) error{Big}!void {
                if (n > 9) return error.Big;
                hits += n;
            }

            const S = struct {
                n: u8,
                fn reset(self: *S) void {
                    bump(self.n) catch unreachable;
                    self.n = 0;
                }
            };

            pub fn main() u8 {
                var s: S = .{ .n = 4 };
                s.reset();
                bump(3) catch unreachable;
                bump(20) catch {};
                return hits + s.n;
            }
            """);
        // Task #130 (std.bit_set.DynamicBitSetUnmanaged.deinit: `self.resize(allocator, 0, false) catch unreachable;`):
        // a void payload has no temp to bind (it had emitted `void __anf0 = …`, CS1547). zig returns 7.
        cs.ShouldNotContain("void __anf");
        cs.ShouldContain("throw new System.Diagnostics.UnreachableException");
    }

    [Theory]
    [InlineData("var n: u8 = 1;\n    while (n < 5) : (n += 1) {\n        blk: {\n            if (n == 2) continue :blk;\n        }\n    }\n    return n;", "`continue :blk` names a labeled block, not a loop")]
    [InlineData("var n: u8 = 1;\n    blk: {\n        if (n == 1) break :blk 5;\n        n += 1;\n    }\n    return n;", "':blk' is a block statement, whose value is void")]
    public void A_labeled_block_statement_refuses_what_zig_refuses(string body, string message)
    {
        // Task #130: zig rejects both ("continue outside of loop or labeled switch expression"; "incompatible types:
        // 'comptime_int' and 'void'"): a block is not a loop, and a block statement's value is void.
        Should.Throw<CompileException>(() => EmitZig("pub fn main() u8 {\n    " + body + "\n}\n")).Message.ShouldContain(message);
    }

    [Fact]
    public void Comptime_print_is_formatted_by_zigs_rules_while_lowering()
    {
        var cs = EmitZig("""
            const std = @import("std");
            const digest_len = 384;

            pub fn main() u8 {
                const a = std.fmt.comptimePrint("[{d:5}|{d:5}|{x:0>4}|{d:*^7}]", .{ @as(i32, 5), 5, @as(u8, 10), -12 });
                const b = std.fmt.comptimePrint("SHA-512/{d}{s:^5}{}", .{ digest_len, "ab", true });
                const c = std.fmt.comptimePrint("{[n]d:.2}{{}}", .{ .n = 7 });
                return @intCast(a.len + b.len + c.len);
            }
            """);
        // Task #128 (std.fmt.comptimePrint: its `*const [count(fmt, args):0]u8` return type needs the formatter run at compile
        // time before the call can bind): formatted while lowering, into the literal a spelled string lowers to. A signed
        // typed int shows `+` under a width, a comptime_int does not; alignment defaults right; a precision is ignored.
        cs.ShouldContain("Libc.L(\"[   +5|    5|000a|**-12**]\\0\"u8)");
        cs.ShouldContain("Libc.L(\"SHA-512/384 ab  true\\0\"u8)");
        cs.ShouldContain("Libc.L(\"7{}\\0\"u8)");
    }

    [Theory]
    [InlineData("var x: u8 = 3;\n    _ = &x;\n    const s = std.fmt.comptimePrint(\"{d}\", .{x});", "argument 0 is not a comptime-known")]
    [InlineData("const s = std.fmt.comptimePrint(\"{d} {d}\", .{5});", "too few arguments")]
    [InlineData("const s = std.fmt.comptimePrint(\"{d}\", .{ 5, 6 });", "unused argument 1 in '{d}'")]
    [InlineData("const s = std.fmt.comptimePrint(\"{}\", .{\"ab\"});", "cannot format a string with `{}`")]
    [InlineData("const s = std.fmt.comptimePrint(\"{d:\\xc3\\xa9>4}\", .{1});", "found a non-ASCII fill")]
    public void Comptime_print_refuses_what_zig_refuses(string decl, string message)
    {
        // Task #128: zig's own errors are "unable to resolve comptime value", "too few arguments", "unused argument in
        // '{d}'", "cannot format slice without a specifier" and "expected . or }, found 'é'".
        Should.Throw<CompileException>(() => EmitZig("const std = @import(\"std\");\npub fn main() u8 {\n    " + decl
            + "\n    return @intCast(s.len);\n}\n")).Message.ShouldContain(message);
    }

    [Fact]
    public void Field_attrs_fold_to_each_fields_spelled_alignment()
    {
        var cs = EmitZig("""
            const S = struct { a: u8, b: u32 align(8), c: u16 = 7 };

            pub fn main() u8 {
                var total: usize = 0;
                inline for (@typeInfo(S).@"struct".field_types, @typeInfo(S).@"struct".field_attrs) |T, attr| {
                    total += attr.@"align" orelse @alignOf(T);
                }
                const spelled = @typeInfo(S).@"struct".field_attrs[1].@"align".?;
                return @intCast(total * 10 + spelled);
            }
            """);
        // Task #108 (std.MultiArrayList's `field_attrs` walk, `f_attrs.@"align" orelse @alignOf(f_type)`): each entry's
        // `.@"align"` is the spelled `align(N)` or null, folded while lowering. zig returns 118.
        cs.ShouldContain("total += (default(ulong?) ?? 1UL);");
        cs.ShouldContain("total += ((ulong?)8UL ?? 4UL);");
    }

    [Fact]
    public void An_untyped_named_literal_has_an_anonymous_struct_type()
    {
        var cs = EmitZig("""
            const S = struct {
                const sizes = blk: {
                    var a: [2]usize = .{ 1, 2 };
                    a[0] = 5;
                    break :blk .{ .bytes = a, .n = 3 };
                };
            };

            pub fn main() u8 {
                var total: usize = S.sizes.n;
                for (S.sizes.bytes) |b| total += b;
                return @intCast(total + S.sizes.bytes[0]);
            }
            """);
        // Task #108 (std.MultiArrayList's unannotated `const sizes = blk: { … break :blk .{ .bytes = …, … }; }`): zig
        // gives the literal an anonymous struct type of its fields; a runtime walk of its array field goes through
        // Unsafe.AsPointer, since a static's fixed buffer does not decay outside `fixed` (CS1666). zig returns 15.
        cs.ShouldContain("unsafe struct Anon__0");
        cs.ShouldContain("public fixed ulong bytes[2];");
        cs.ShouldContain("Unsafe.AsPointer(ref S__sizes__static.bytes[0])");
    }

    [Fact]
    public void A_comptime_int_block_const_meets_a_typed_peer_at_its_width()
    {
        var cs = EmitZig("""
            const S = struct {
                const init_capacity: comptime_int = init: {
                    var max: comptime_int = 1;
                    for ([_]u8{ 2, 8, 4 }) |x| max = @max(max, x);
                    break :init @max(1, 64 / max);
                };
                fn grow(minimum: usize) usize {
                    return minimum +| (minimum / 2 + init_capacity);
                }
            };

            pub fn main() u8 {
                return @intCast(S.grow(10));
            }
            """);
        // Task #108 (std.MultiArrayList's `const init_capacity: comptime_int = init: { … }` in `minimum +| (minimum / 2
        // + init_capacity)`): the evaluated block is the literal, not a 128-bit static. zig returns 23.
        cs.ShouldContain("ZigMath.SatAdd(minimum, minimum / (ulong)(2) + (ulong)(8))");
    }

    [Fact]
    public void An_inline_for_walks_arrays_in_lockstep_with_a_comptime_list()
    {
        var cs = EmitZig("""
            const E = enum(u8) { a, b, c };

            fn weight(comptime e: E) u32 {
                return switch (e) {
                    .a => 1,
                    .b => 10,
                    .c => 100,
                };
            }

            const D = struct { x: u64, y: u64 };

            pub fn main() u8 {
                const in = [_]u32{ 1, 2, 3 };
                var out: [3]u32 = undefined;
                inline for (in, &out, [_]type{ u8, u16, u32 }) |x, *o, t| {
                    o.* = x * @sizeOf(t);
                }
                var total: u32 = out[0] + out[1] + out[2];
                inline for (0..3) |i| {
                    const e = @as(E, @enumFromInt(i));
                    total += weight(e);
                }
                return @intCast(total + @bitSizeOf(D) / 8);
            }
            """);
        // Task #108 (std.MultiArrayList.Slice.subslice's `inline for (s.ptrs, &ptrs, field_types) |in, *out, field_type|`):
        // each copy binds an array element by value or `*` pointer beside the comptime type; an enum const folded from
        // the index is a comptime argument; a struct of uniform scalars has an exact `@bitSizeOf`. zig returns 144.
        cs.ShouldContain("uint* o = &@out[0];");
        cs.ShouldContain("total += weight__2();");
        cs.ShouldContain("(128 / 8)");
    }

    [Theory]
    [InlineData("const N = struct { a: u8, b: u32 };\npub fn main() u8 {\n    return @intCast(@bitSizeOf(N));\n}\n", "only a scalar")]
    [InlineData("const S = struct { a: u8 = 1 };\npub fn main() u8 {\n    const p = @typeInfo(S).@\"struct\".field_attrs[0].default_value_ptr;\n    return if (p == null) 0 else 1;\n}\n", "default_value_ptr")]
    public void Field_attrs_and_bit_size_stay_loud_where_dotcc_cannot_be_exact(string source, string message)
    {
        // Task #108: a struct of mixed field sizes may be laid out differently by zig (reordered) and dotcc, so its
        // `@bitSizeOf` stays the S7 cut; a defaulted field's pointer to its comptime default is not modeled.
        Should.Throw<Exception>(() => EmitZig(source)).Message.ShouldContain(message);
    }

    [Fact]
    public void A_type_const_reads_a_nested_structs_fields_before_its_body_registers()
    {
        var cs = EmitZig("""
            fn Mal(comptime T: type) type {
                const names = @typeInfo(T).@"struct".field_names;
                return struct {
                    len: usize = names.len,
                };
            }

            fn Custom(comptime K: type, comptime V: type, comptime store_hash: bool) type {
                return struct {
                    entries: DataList = .{},
                    pub const Data = struct {
                        hash: Hash,
                        key: K,
                        value: V,
                    };
                    pub const DataList = Mal(Data);
                    pub const Hash = if (store_hash) u32 else void;
                };
            }

            pub fn main() u8 {
                const a: Custom(u32, u16, false) = .{};
                const b: Custom(u32, u16, true) = .{};
                return @intCast(a.entries.len * 10 + b.entries.len);
            }
            """);
        // Task #135 (std.array_hash_map's `DataList = std.MultiArrayList(Data)` over `Data { hash: Hash, … }`, with `Hash`
        // declared after both): the nested body registers on first demand, and the type const it names resolves on
        // demand too. zig returns 33.
        cs.ShouldContain("new Mal__Custom__u32_u16_0__Data { len = ");
        cs.ShouldContain("unsafe struct Mal__Custom__u32_u16_1__Data");
    }

    [Fact]
    public void Void_fields_static_alloc_calls_and_exhaustive_returning_switches()
    {
        var cs = EmitZig("""
            const Header = struct {
                n: u8,
                fn alloc(n: u8) Header {
                    return .{ .n = n };
                }
            };

            const Kind = enum { a, b, c };

            fn size(k: Kind) usize {
                switch (k) {
                    .a => return 1,
                    .b => return 2,
                    .c => return 4,
                }
            }

            fn Box(comptime T: type, comptime keep: bool) type {
                return struct {
                    tag: Tag,
                    val: T,
                    const Tag = if (keep) u32 else void;
                    fn same(self: @This(), t: Tag) bool {
                        return self.tag == t;
                    }
                    fn pick(self: @This(), other: T) T {
                        return if (keep) self.val else other;
                    }
                };
            }

            pub fn main() u8 {
                const h = Header.alloc(5);
                var b: Box(u8, false) = .{ .tag = {}, .val = 7 };
                b.tag = {};
                const c: Box(u8, true) = .{ .tag = 9, .val = 1 };
                var total: usize = h.n + size(.c) + @sizeOf(void) + @bitSizeOf(void);
                if (b.same({})) total += 10;
                if (c.same(9)) total += 100;
                total += b.pick(20) + c.pick(20);
                return @intCast(total + b.val + c.val);
            }
            """);
        // Task #135 (std.array_hash_map shapes): `Header.alloc(n)` is a static call, not an allocator method; an enum
        // switch whose prongs all return gets an unreachable default for C#; `@sizeOf(void)` / `@bitSizeOf(void)` are 0;
        // `void == void` is true; a store to a `void` field keeps only its effects; a comptime bool seed picks an arm.
        // zig returns 148.
        cs.ShouldContain("Header h = Header_alloc(5);");
        cs.ShouldContain("ulong total = h.n + size(Kind.c) + 0UL + (ulong)(0);");
        cs.ShouldContain("internal static unsafe CBool Box__u8_0_same(Box__u8_0 self)\n    {\n        return true;");
    }

    [Fact]
    public void Unsigned_element_field_and_deref_reads_divide_with_slash_and_percent()
    {
        var cs = EmitZig("""
            const Box = struct { n: u16 };

            pub fn main() u8 {
                var out = [_]u16{ 0, 0x1e9, 3 };
                _ = &out;
                const e: []const u8 = "a7";
                var b = Box{ .n = 47 };
                _ = &b;
                const p = &b;
                return @intCast((out[1] & 0xff) % 10 + (e[1] - '0') % 10 + p.n % 10 + b.n / 7 + p.*.n % 3);
            }
            """);
        // Task #136 (std.base64 and std.unicode shapes): an index, field or deref read keeps its declared
        // unsigned zig type, so `%` and `/` over it are not refused as signed. zig returns 25.
        cs.ShouldContain("return (byte)((@out[1] & 255) % 10 + (55u - 48) % 10 + p->n % 10 + b.n / 7 + (*p).n % 3);");
    }

    [Fact]
    public void A_signed_element_read_is_still_refused_under_percent()
    {
        var ex = Should.Throw<CompileException>(() => EmitZig("""
            pub fn main() u8 {
                var a = [_]i32{ -7, 2 };
                _ = &a;
                return @intCast(@abs(a[0] % 3));
            }
            """));
        // Task #136: zig rejects `%` on a runtime `i32` element; the declared type now reaches the check.
        ex.Message.ShouldContain("signed integers and floats must use @rem or @mod");
    }

    [Fact]
    public void Bytes_as_slice_reads_an_array_pointer_or_a_slice_as_wider_elements()
    {
        var cs = EmitZig("""
            const std = @import("std");
            pub fn main() u8 {
                var raw = [_]u8{ 1, 0, 2, 0, 3, 0, 4, 0 };
                const words = std.mem.bytesAsSlice(u16, &raw);
                words[1] = 7;
                const view = std.mem.bytesAsSlice(u32, raw[0..]);
                const b = [_]u8{ 1, 0, 2, 0, 3, 0 };
                const s = std.mem.bytesAsSlice(u16, &b);
                return @intCast(s.len * 10 + s[2] + raw[2] + view.len + (view[0] >> 16));
            }
            """);
        // Task #137: std.mem.bytesAsSlice(T, bytes) is curated (its return type is reified through `@Pointer`); the
        // length is `bytes.len / @sizeOf(T)`, and a slice built in place gives its own parts. zig returns 49.
        cs.ShouldContain("Slice<ushort> words = new Slice<ushort>((ushort*)raw, 8UL / ((ulong)(sizeof(ushort))));");
        cs.ShouldContain("Slice<uint> view = new Slice<uint>((uint*)(raw + 0), (8UL - (ulong)0) / ((ulong)(sizeof(uint))));");
        cs.ShouldContain("ConstSlice<ushort> s = new ConstSlice<ushort>((ushort*)b, 6UL / ((ulong)(sizeof(ushort))));");
    }

    [Fact]
    public void Byte_view_calls_evaluate_a_side_effecting_slice_once()
    {
        var cs = EmitZig("""
            const std = @import("std");
            var calls: u8 = 0;
            var store = [_]u8{ 5, 0, 6, 0 };
            var halves = [_]u16{ 0x0102, 0x0304 };
            fn bytes() []u8 {
                calls += 1;
                return store[0..];
            }
            fn words() []u16 {
                calls += 1;
                return halves[0..];
            }
            pub fn main() u8 {
                const w = std.mem.bytesAsSlice(u16, bytes());
                const b = std.mem.sliceAsBytes(words());
                return @intCast(w.len * 10 + w[1] + b.len + b[3] + calls * 20);
            }
            """);
        // Task #137: a call as the slice operand of bytesAsSlice / sliceAsBytes is hoisted to a temp, so it runs once
        // (sliceAsBytes read `.Ptr` and `.Len` off the call separately before). zig returns 73.
        cs.ShouldContain("Slice<byte> __anf0 = bytes();\n        Slice<ushort> w = new Slice<ushort>((ushort*)__anf0.Ptr, __anf0.Len / ((ulong)(sizeof(ushort))));");
        cs.ShouldContain("Slice<ushort> __anf1 = words();\n        Slice<byte> b = new Slice<byte>((byte*)__anf1.Ptr, __anf1.Len * ((ulong)(sizeof(ushort))));");
    }

    [Fact]
    public void A_u64_comptime_argument_past_i64_keys_and_seeds_a_type_returning_generic()
    {
        var cs = EmitZig("""
            fn Hash(comptime T: type, comptime prime: T, comptime offset: T) type {
                return struct {
                    value: T = offset,
                    const top: T = offset >> 60;
                    fn high() bool {
                        return offset > 0x8000000000000000;
                    }
                    fn mix(self: *@This(), b: u8) void {
                        self.value ^= b;
                        self.value *%= prime;
                    }
                };
            }
            const A = Hash(u64, 0x100000001b3, 0xcbf29ce484222325);
            const B = Hash(u64, 3, 0xffffffffffffffff);
            pub fn main() u8 {
                var a: A = .{};
                a.mix('a');
                var b: B = .{};
                b.mix(1);
                const t: u8 = @intCast(A.top);
                return @as(u8, @truncate(a.value)) +% t +% @as(u8, @intFromBool(A.high())) +% @as(u8, @truncate(b.value >> 56));
            }
            """);
        // Task #139 (std.hash.Fnv1a_64 = Fnv1a(u64, 0x100000001b3, 0xcbf29ce484222325)): a `u64` comptime argument past
        // i64 is read by the interpreter, keys the instance by its unsigned value and is spelled back unsigned
        // wherever the body reads it, in comparisons and shifts too. zig returns 152.
        cs.ShouldContain("Hash__u64_1099511628211_14695981039346656037 a = new Hash__u64_1099511628211_14695981039346656037 { value = 14695981039346656037UL };");
        cs.ShouldContain("Hash__u64_3_18446744073709551615 b = new Hash__u64_3_18446744073709551615 { value = 18446744073709551615UL };");
        cs.ShouldContain("byte t = unchecked((byte)(14695981039346656037UL >> 60));");
        cs.ShouldContain("return ((CBool)(14695981039346656037UL > 9223372036854775808UL));");
    }

    [Fact]
    public void Curated_array_list_insert_slice_takes_an_index_and_a_slice()
    {
        var cs = EmitZig("""
            const std = @import("std");
            pub fn main() !u8 {
                var buf: [256]u8 = undefined;
                var fba = std.heap.FixedBufferAllocator.init(&buf);
                const gpa = fba.allocator();
                var l: std.ArrayList(u16) = .empty;
                defer l.deinit(gpa);
                try l.appendSlice(gpa, &.{ 1, 5 });
                try l.insertSlice(gpa, 1, &.{ 2, 3, 4 });
                try l.insertSlice(gpa, 0, &.{0});
                try l.insertSlice(gpa, l.items.len, &.{ 6, 7 });
                const more = [_]u16{ 8, 9 };
                try l.insertSlice(gpa, 8, &more);
                var acc: u16 = 0;
                for (l.items) |v| acc = acc * 3 +% v;
                return @truncate(acc +% @as(u16, @intCast(l.items.len)));
            }
            """);
        // Task #138: `list.insertSlice(gpa, i, items)` maps onto the runtime ZigList, its slice argument coerced as
        // appendSlice's is (an anonymous list literal's address, `&arr`). zig returns 175.
        cs.ShouldContain("_ = ErrUnion.Try(l.InsertSlice(ZigAlloc.FbaAllocator(&fba), 1, new Slice<ushort>(__cl1, 3UL), 1));");
        cs.ShouldContain("_ = ErrUnion.Try(l.InsertSlice(ZigAlloc.FbaAllocator(&fba), l.Items.Len, new Slice<ushort>(__cl3, 2UL), 1));");
    }

    [Fact]
    public void An_empty_literal_at_a_nonzero_extent_is_still_rejected()
    {
        Should.Throw<Exception>(() => EmitZig("""
            pub fn main() u8 {
                const a: [3]u8 = [3]u8{};
                return a[0];
            }
            """)).Message.ShouldContain("empty array literal");
    }
}
