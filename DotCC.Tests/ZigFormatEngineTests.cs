#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for what std.fmt.bufPrint's path through std.Io.Writer.print / printValue / printInt needed (road-to-zig-std
/// G3, task #10): a comptime OPTIONAL bound from a switch (<c>arg_pos</c>), a tuple field named by a member-list index
/// (<c>@field(args, field_names[i])</c>), a switch over a TYPE (<c>switch (@TypeOf(value))</c>), the prong forms
/// <c>=&gt; if (c) switch …</c> / <c>=&gt; if (x) |v| return …</c> / <c>=&gt; for (…) …</c>, a comptime-settled
/// <c>or</c> that never lowers its right side (std.math.cast), and a field of a default-initialized module const
/// (<c>std.options.fmt_max_depth</c>). End-to-end in the <c>prong_forms_type_switch</c> and
/// <c>comptime_optional_switch</c> zig-oracle programs.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigFormatEngineTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigfe-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Statement_prongs_may_be_an_if_switch_a_returning_capture_if_or_a_loop()
    {
        var cs = EmitZig("""
            fn f(x: u8, fmtlen: u8) u8 {
                var r: u8 = 0;
                switch (fmtlen) {
                    3 => if (x > 1) switch (x) {
                        2 => r = 5,
                        else => r = 6,
                    },
                    4 => if (maybe(x)) |v| return v,
                    else => for (0..x) |_| {
                        r += 1;
                    },
                }
                return r;
            }
            fn maybe(x: u8) ?u8 {
                return if (x > 2) x else null;
            }
            pub fn main() u8 {
                return f(2, 3) + f(3, 4) + f(4, 9);
            }
            """);
        cs.ShouldMatch(@"case 3:\s*if \(Cond\.B\(\(\(CBool\)\(x > 1\)\)\)\)\s*\{\s*switch \(x\)");
        cs.ShouldMatch(@"byte v = __cap\.Value;\s*return v;");
        cs.ShouldContain("for (ulong _ = 0; Cond.B(((CBool)(_ < (ulong)x))); _++)");
    }

    [Fact]
    public void A_switch_over_a_type_selects_its_prong_at_lowering_time()
    {
        var cs = EmitZig("""
            fn kind(v: anytype) u8 {
                switch (@TypeOf(v)) {
                    u8, u16 => return 1,
                    comptime_int => return 2,
                    else => return 3,
                }
            }
            pub fn main() u8 {
                return kind(@as(u8, 1)) + kind(7) + kind(@as(i64, 1));
            }
            """);
        cs.ShouldMatch(@"byte kind__u8\(byte v\)\s*\{\s*return 1;");
        cs.ShouldMatch(@"byte kind__ci7\(\)\s*\{\s*return 2;");
        cs.ShouldMatch(@"byte kind__i64\(long v\)\s*\{\s*return 3;");
    }

    [Fact]
    public void A_comptime_switch_with_a_null_prong_binds_a_comptime_optional()
    {
        var cs = EmitZig("""
            const ArgState = struct {
                next_arg: usize = 0,
                args_len: usize,
                pub fn nextArg(self: *@This(), arg_index: ?usize) ?usize {
                    const next_index = arg_index orelse init: {
                        const arg = self.next_arg;
                        self.next_arg += 1;
                        break :init arg;
                    };
                    if (next_index >= self.args_len) return null;
                    return next_index;
                }
            };
            const Spec = union(enum) { none, number: usize };
            fn parse(comptime s: []const u8) Spec {
                if (s.len == 0) return .{ .none = {} };
                return .{ .number = s.len };
            }
            pub fn main() u8 {
                comptime var st: ArgState = .{ .args_len = 1 };
                const p = comptime parse("");
                const arg_pos = comptime switch (p) {
                    .none => null,
                    .number => |pos| pos,
                };
                const a = comptime st.nextArg(arg_pos) orelse @compileError("too few arguments");
                return @intCast(a + 42);
            }
            """);
        cs.ShouldContain("ulong a = 0UL;");   // the untaken `@compileError` fallback is never analysed
        cs.ShouldNotContain("arg_pos");       // a comptime optional has no runtime declaration
    }

    [Fact]
    public void A_tuple_field_is_named_by_a_member_list_index()
    {
        var cs = EmitZig("""
            fn pick(args: anytype) u8 {
                const info = @typeInfo(@TypeOf(args));
                const field_names = info.@"struct".field_names;
                const i = 1;
                return @field(args, field_names[i]);
            }
            pub fn main() u8 {
                return pick(.{ @as(u8, 1), @as(u8, 41) }) + 1;
            }
            """);
        cs.ShouldContain("return args.Item2;");
    }

    [Fact]
    public void A_comptime_settled_or_never_lowers_its_right_side()
    {
        // std.math.cast's `is_comptime or maxInt(@TypeOf(x)) > …`: over a comptime_int the right side would ask
        // `@typeInfo(comptime_int).int`, which zig never analyses and dotcc would reject.
        var cs = EmitZig("""
            fn wide(x: anytype) bool {
                const is_comptime = @TypeOf(x) == comptime_int;
                return is_comptime or @typeInfo(@TypeOf(x)).int.bits > 8;
            }
            pub fn main() u8 {
                return if (wide(5)) 42 else 0;
            }
            """);
        cs.ShouldMatch(@"CBool wide__ci5\(\)\s*\{\s*return true;");
    }

    [Fact]
    public void The_format_engine_s_stores_and_returns_compile_to_valid_csharp()
    {
        // What std.fmt's integer printing emitted as C# that did not build (task #50): a compound assignment through
        // `.?` (C#'s Nullable.Value is read-only), an array stored through a slice deref (`buf[i..][0..2].* = …`), an
        // array returned from a slice deref (std.fmt.digits2), and an `unreachable` switch-expression arm.
        var cs = EmitZig("""
            fn digits2(value: u8) [2]u8 {
                return "00010203040506070809101112131415161718192021222324252627282930313233343536373839404142"[value * 2 ..][0..2].*;
            }
            fn toChar(d: u8) u8 {
                return switch (d) {
                    0...9 => d + '0',
                    else => unreachable,
                };
            }
            pub fn main() u8 {
                var r: ?u32 = 3;
                r.? *= 10;
                r.? += 4;
                var buf: [4]u8 = .{ 0, 0, 0, 0 };
                buf[1..][0..2].* = digits2(42);
                return @intCast(r.? + buf[1] - '0' + buf[2] - '0' + toChar(1) - '0');
            }
            """);
        cs.ShouldContain("r = (uint?)(uint)(r.Value * (uint)(10));");
        cs.ShouldContain("ZigMem.CopyForwards<byte>(new Slice<byte>(new Slice<byte>(buf + 1,");
        cs.ShouldMatch(@"CopyArrayResult<byte>\(.*\)\.Ptr, 2\);");
        cs.ShouldContain("_ => throw new System.Diagnostics.UnreachableException(\"unreachable() reached\")");
    }

    [Fact]
    public void A_field_of_a_default_initialized_module_const_is_its_default_alone()
    {
        // std.zig's `pub const options: Options = if (@hasDecl(root, "std_options")) root.std_options else .{};`:
        // the synthetic `root` declares nothing, so `.{}` is taken and `lib.options.depth` is the field's default. The
        // struct's other field (an `@EnumLiteral()`) never has to lower.
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zigfe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "lib.zig"), """
                const root = @import("root");
                pub const Options = struct {
                    scope: @EnumLiteral() = .x,
                    depth: usize = 3,
                };
                pub const options: Options = if (@hasDecl(root, "my_options")) root.my_options else .{};
                """);
            var main = Path.Combine(dir, "main.zig");
            File.WriteAllText(main, """
                const lib = @import("lib.zig");
                pub fn main() u8 {
                    return @intCast(lib.options.depth + 39);
                }
                """);
            var cs = Compiler.EmitCSharp(new[] { main });
            cs.ShouldContain("return unchecked((byte)(3 + 39));");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
