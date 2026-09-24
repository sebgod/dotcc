#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for std.fmt.parse_float's walls 10 to 13 (road-to-zig-std, task #59): a labeled switch expression
/// (<c>r: switch (x) { 0 =&gt; 10, … =&gt; { break :r v; } }</c>, std.math.shl), whose bare value prongs are the switch's
/// value while a switch nested in a prong keeps its own; <c>const info = infoOf(T);</c> binding as a comptime aggregate
/// when the result struct is comptime-only (FloatInfo's <c>comptime_int</c> fields), so a field feeds a
/// <c>comptime precision</c> argument, and a runtime struct does not; a comptime int switch folding when a prong is a
/// <c>@compileError</c> (std.math.floatMantissaBits); and a reified struct's consts registered before its fields
/// (Decimal's <c>digits: [max_digits]u8</c>). End to end in the <c>labeled_switch</c> and <c>parse_float_shapes</c>
/// zig-oracle programs.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigParseFloatShapesTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigpf-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private const string LabeledSwitchProgram = """
        fn shiftCap(comptime T: type, amt: u32) u32 {
            const capped = capped: switch (@typeInfo(T)) {
                .int => |info| {
                    if (amt < info.bits) break :capped amt;
                    break :capped info.bits - 1;
                },
                else => 0,
            };
            return capped;
        }

        fn classify(x: u8) u8 {
            const r = r: switch (x) {
                0 => 10,
                1...9 => {
                    if (x == 5) break :r 55;
                    break :r x * 2;
                },
                else => 99,
            };
            return r;
        }

        var hits: u8 = 0;

        fn nested(x: u8) u8 {
            const r: u8 = r: switch (x) {
                0 => {
                    switch (x) {
                        0 => {
                            hits += 1;
                        },
                        else => {
                            hits += 2;
                        },
                    }
                    break :r 4;
                },
                1 => unreachable,
                else => 6,
            };
            return r;
        }

        pub fn main() u8 {
            return @intCast(shiftCap(u8, 3) + shiftCap(u8, 40) + classify(0) + classify(5) + classify(7) + classify(200) % 100 + nested(0) + nested(9) + hits);
        }
        """;

    private const string ShapesProgram = """
        const Info = struct { explicit_bits: comptime_int, bias: comptime_int };

        fn mantissaBits(comptime T: type) comptime_int {
            return switch (@typeInfo(T).float.bits) {
                32 => 23,
                64 => 52,
                else => @compileError("unknown floating point type " ++ @typeName(T)),
            };
        }

        fn infoOf(comptime T: type) Info {
            return switch (T) {
                f32 => .{ .explicit_bits = mantissaBits(T), .bias = 127 },
                f64 => .{ .explicit_bits = mantissaBits(T), .bias = 1023 },
                else => @compileError("no info"),
            };
        }

        fn product(comptime precision: u8, w: u64) u64 {
            return w * precision;
        }

        fn Decimal(comptime T: type) type {
            const Wide = if (T == f64) u64 else u32;
            return struct {
                const Self = @This();
                pub const max_digits = if (Wide == u64) 12 else 6;
                digits: [max_digits]u8,
                count: usize,

                pub fn init() Self {
                    var v: Self = undefined;
                    v.count = 0;
                    return v;
                }
            };
        }

        fn capped(comptime T: type, amt: u32) u32 {
            return capped: switch (@typeInfo(T)) {
                .int => |info| {
                    if (amt < info.bits) break :capped amt;
                    break :capped info.bits - 1;
                },
                else => 0,
            };
        }

        pub fn main() u8 {
            const info = infoOf(f64);
            const p = product(info.explicit_bits + 3, 2);
            var d = Decimal(f64).init();
            d.digits[11] = 4;
            d.count = d.digits.len;
            return @intCast(p + d.count + d.digits[11] + capped(u8, 3) + capped(u8, 40) + capped(bool, 1));
        }
        """;

    [Fact]
    public void A_labeled_switch_yields_its_value_prongs_and_its_breaks()
    {
        var cs = EmitZig(LabeledSwitchProgram);
        cs.ShouldMatch(@"case 0:\s*\{\s*__blk\d+ = 10;\s*goto __blk\d+_end;");
        cs.ShouldContain("__blk0 = 55;");
    }

    [Fact]
    public void A_switch_nested_in_a_labeled_switch_prong_keeps_its_own_prongs()
    {
        var cs = EmitZig(LabeledSwitchProgram);
        cs.ShouldContain("hits += (byte)(1);");
        cs.ShouldContain("hits += (byte)(2);");
    }

    [Fact]
    public void A_labeled_switch_over_a_comptime_subject_folds_to_the_taken_prong()
    {
        var cs = EmitZig(ShapesProgram);
        cs.ShouldMatch(@"uint capped__u8\(uint amt\)\s*\{\s*uint __blk0 = default\(uint\);\s*\{\s*if \(Cond\.B\(\(\(CBool\)\(amt < \(uint\)\(8\)\)\)\)\)");
    }

    [Fact]
    public void A_comptime_only_struct_call_binds_at_compile_time_and_feeds_a_comptime_argument()
    {
        var cs = EmitZig(ShapesProgram);
        cs.ShouldContain("ulong p = product__55(2);");   // 52 mantissa bits + 3
    }

    [Fact]
    public void A_runtime_struct_call_is_not_comptime()
    {
        Should.Throw<Exception>(() => EmitZig("""
            const Info = struct { explicit_bits: u8, bias: i16 };

            fn mantissaBits(comptime T: type) comptime_int {
                return switch (@typeInfo(T).float.bits) {
                    32 => 23,
                    64 => 52,
                    else => @compileError("unknown floating point type " ++ @typeName(T)),
                };
            }

            fn infoOf(comptime T: type) Info {
                return switch (T) {
                    f32 => .{ .explicit_bits = mantissaBits(T), .bias = 127 },
                    f64 => .{ .explicit_bits = mantissaBits(T), .bias = 1023 },
                    else => @compileError("no info"),
                };
            }

            fn product(comptime precision: u8, w: u64) u64 {
                return w * precision;
            }

            fn Decimal(comptime T: type) type {
                const Wide = if (T == f64) u64 else u32;
                return struct {
                    const Self = @This();
                    pub const max_digits = if (Wide == u64) 12 else 6;
                    digits: [max_digits]u8,
                    count: usize,

                    pub fn init() Self {
                        var v: Self = undefined;
                        v.count = 0;
                        return v;
                    }
                };
            }

            fn capped(comptime T: type, amt: u32) u32 {
                return capped: switch (@typeInfo(T)) {
                    .int => |info| {
                        if (amt < info.bits) break :capped amt;
                        break :capped info.bits - 1;
                    },
                    else => 0,
                };
            }

            pub fn main() u8 {
                const info = infoOf(f64);
                const p = product(info.explicit_bits + 3, 2);
                var d = Decimal(f64).init();
                d.digits[11] = 4;
                d.count = d.digits.len;
                return @intCast(p + d.count + d.digits[11] + capped(u8, 3) + capped(u8, 40) + capped(bool, 1));
            }
            """)).Message.ShouldContain("must be a compile-time-known integer constant");
    }

    [Fact]
    public void A_reified_struct_const_sizes_its_array_field()
    {
        var cs = EmitZig(ShapesProgram);
        cs.ShouldMatch(@"unsafe struct Decimal__f64\s*\{[^}]*public fixed byte digits\[12\];");
    }
}
