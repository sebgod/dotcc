#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for the bricks on <c>std.Io.Writer.print</c>'s path (road-to-zig-std G3, bufPrint): a
/// <c>comptime switch</c> / <c>comptime if</c> in value position; <c>&amp;.{ … }</c> at a <c>*const S</c> sink
/// placed in static storage (<c>Writer.fixed</c>'s vtable); a lazy module's function named as a value; an
/// <c>enum(u64)</c> member of <c>maxInt(u64)</c> (<c>std.Io.Limit</c>), evaluated during registration; and
/// a statement <c>unreachable</c>. End-to-end in the <c>comptime_value_forms</c> and <c>vtable_literal</c>
/// zig-oracle programs.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigPrintPathTests
{
    private static string EmitMulti(string main, params (string name, string source)[] siblings)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zigpp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (name, source) in siblings) { File.WriteAllText(Path.Combine(dir, name), source); }
            var path = Path.Combine(dir, "main.zig");
            File.WriteAllText(path, main);
            return Compiler.EmitCSharp(new[] { path });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private const string Writer = """
        const Writer = @This();

        vtable: *const VTable,
        n: u8,

        pub const VTable = struct {
            drain: *const fn (w: *Writer, x: u8) u8,
            flush: *const fn (w: *Writer) u8 = noFlush,
        };

        pub fn fixed(n: u8) Writer {
            return .{ .vtable = &.{ .drain = fixedDrain }, .n = n };
        }

        fn fixedDrain(w: *Writer, x: u8) u8 {
            return w.n + x;
        }

        fn noFlush(w: *Writer) u8 {
            _ = w;
            return 1;
        }
        """;

    [Fact]
    public void A_comptime_known_struct_literal_address_is_a_static_and_names_functions_as_values()
    {
        var cs = EmitMulti("""
            const Writer = @import("Writer.zig");
            pub fn main() u8 {
                var a: Writer = .fixed(30);
                return a.vtable.drain(&a, 12);
            }
            """, ("Writer.zig", Writer));
        cs.ShouldContain("public static unsafe Writer__VTable Writer__anon0 = new Writer__VTable { drain = &Writer__fixedDrain, flush = &Writer__noFlush };");
        cs.ShouldContain("Unsafe.AsPointer(ref Writer__anon0)");
    }

    [Fact]
    public void A_struct_literal_address_with_a_runtime_field_is_a_loud_cut()
    {
        var ex = Should.Throw<CompileException>(() => EmitMulti("""
            const V = struct { n: u8 };
            const W = struct { v: *const V };
            fn make(n: u8) W {
                return .{ .v = &.{ .n = n } };
            }
            pub fn main() u8 {
                return make(42).v.n;
            }
            """));
        ex.Message.ShouldContain("with a runtime-known field");
    }

    [Fact]
    public void Comptime_value_forms_an_enum_u64_max_member_and_unreachable_lower()
    {
        var cs = EmitMulti("""
            const m = @import("m.zig");
            const Limit = enum(u64) {
                nothing = 0,
                unlimited = m.maxInt(u64),
                _,
            };
            fn pick(comptime k: u8) u8 {
                const v = comptime switch (k) {
                    1 => 30,
                    else => 0,
                };
                const w: u8 = comptime if (k == 1) 2 else 0;
                return v + w;
            }
            fn check(x: u8) u8 {
                switch (x) {
                    0 => unreachable,
                    else => {},
                }
                return x;
            }
            pub fn main() u8 {
                const l: Limit = .unlimited;
                _ = l;
                return pick(1) + check(10);
            }
            """, ("m.zig", """
            pub fn maxInt(comptime T: type) comptime_int {
                const info = @typeInfo(T).int;
                return (1 << (info.bits - @intFromBool(info.signedness == .signed))) - 1;
            }
            """));
        cs.ShouldContain("unlimited = 18446744073709551615,");
        cs.ShouldContain("throw new System.Diagnostics.UnreachableException(");
        cs.ShouldNotContain("maxInt__");   // evaluated during registration, never emitted
    }

    [Fact]
    public void A_tuple_types_member_lists_are_its_positions()
    {
        // `@typeInfo(@TypeOf(args)).@"struct".field_names` over the args of a format call (zig 0.17's
        // parallel arrays; the CI oracle's 0.16.0 has none, so this is pinned here).
        var cs = EmitMulti("""
            fn count(args: anytype) u8 {
                const info = @typeInfo(@TypeOf(args));
                const names = info.@"struct".field_names;
                const n: u8 = names.len;
                return n + names[1][0];
            }
            pub fn main() u8 {
                return count(.{ @as(u8, 1), @as(u16, 2) });
            }
            """);
        cs.ShouldContain("byte n = 2;");
        cs.ShouldContain("49");   // '1', the second field's name
    }

    [Fact]
    public void A_module_qualified_type_alias_carries_its_declared_width_and_a_local_const_folds()
    {
        var cs = EmitMulti("""
            const m = @import("m.zig");
            fn check(comptime n: usize) u8 {
                const max = @typeInfo(m.Word).int.bits;
                if (n > max) {
                    @compileError("too many");
                }
                @setEvalBranchQuota(@as(comptime_int, n) * 1000);
                return @intCast(max + n);
            }
            pub fn main() u8 {
                const w: m.Word = 8;
                return check(2) + @as(u8, @intCast(w));
            }
            """, ("m.zig", "pub const Word = u21;\n"));
        cs.ShouldContain("int max = 21;");   // the SPELLED width, although u21 lowers to uint
        cs.ShouldContain("uint w = 8;");
    }
}
