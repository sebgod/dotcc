#nullable enable

using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Agreement pins for the one Zig comptime evaluator (<c>ZigLowering.TryEvalComptime</c>, GH #232): every position that
/// reads a comptime STRING sees the same decoded bytes. Each pin spells the string with an escape (<c>"\x61"</c> is
/// <c>a</c>) or binds it through a local, the two shapes on which the old per-position helpers disagreed (one returned the
/// raw source text, another only resolved a literal).
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigComptimeEvalTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigcev-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private const string Pair = """
        const S = struct { a: u32, ab: u32 };
        """;

    [Fact]
    public void An_escaped_name_reaches_field()
    {
        var cs = EmitZig(Pair + """

            pub fn main() u8 {
                const s = S{ .a = 7, .ab = 9 };
                return @intCast(@field(s, "\x61"));
            }
            """);
        cs.ShouldContain("s.a");
    }

    [Fact]
    public void An_escaped_name_reaches_hasField()
    {
        // The `else` arm is a compile error: it must not be lowered, so `@hasField` has to see the name `a`.
        var cs = EmitZig(Pair + """

            pub fn main() u8 {
                if (@hasField(S, "\x61")) { return 1; } else { @compileError("no field a"); }
            }
            """);
        cs.ShouldNotBeNull();
    }

    [Fact]
    public void An_escaped_compileError_message_is_decoded()
    {
        var ex = Should.Throw<DotCC.Ir.IrUnsupportedException>(() => EmitZig("""
            pub fn main() u8 {
                @compileError("\x61bc");
            }
            """));
        ex.Message.ShouldContain("abc");
        ex.Message.ShouldNotContain("\\x61");
    }

    [Fact]
    public void A_byte_compare_of_an_escaped_string_folds()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                const s = "\x61bc";
                if (s[0] == 'a') { return 1; } else { @compileError("s[0] is not a"); }
            }
            """);
        cs.ShouldNotBeNull();
    }

    [Fact]
    public void A_length_compare_of_an_escaped_string_folds()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                const s = "\x61bc";
                if (s.len == 3) { return 1; } else { @compileError("s.len is not 3"); }
            }
            """);
        cs.ShouldNotBeNull();
    }

    [Fact]
    public void A_concat_of_a_comptime_local_names_a_field()
    {
        var cs = EmitZig(Pair + """

            pub fn main() u8 {
                const s = S{ .a = 7, .ab = 9 };
                const first = "\x61";
                return @intCast(@field(s, first ++ "b"));
            }
            """);
        cs.ShouldContain("s.ab");
    }

    [Fact]
    public void A_concat_of_an_inline_for_capture_names_a_field()
    {
        var cs = EmitZig("""
            const S = struct { a: u32 };
            pub fn main() u8 {
                const s = S{ .a = 7 };
                var t: u32 = 0;
                inline for (@typeInfo(S).@"struct".field_names) |f| {
                    t += @field(s, f ++ "");
                }
                return @intCast(t);
            }
            """);
        cs.ShouldContain("s.a");
    }

    [Fact]
    public void Comptime_print_formats_a_local_escaped_format_string()
    {
        var cs = EmitZig("""
            const std = @import("std");
            pub fn main() u8 {
                const f = "\x61{d}";
                const r = std.fmt.comptimePrint(f, .{1});
                return @intCast(r.len);
            }
            """);
        cs.ShouldContain("Libc.L(\"a1\\0\"u8)");
    }

    [Fact]
    public void One_comptime_value_reads_as_an_int_width()
    {
        var cs = EmitZig("""
            const w = 4 + 4;
            pub fn main() u8 {
                const x: @Int(.unsigned, w) = 200;
                return @intCast(x);
            }
            """);
        cs.ShouldContain("byte");
    }

    [Fact]
    public void A_comptime_int_const_sets_the_eval_branch_quota()
    {
        var cs = EmitZig("""
            const q = 1000 * 2;
            pub fn main() u8 {
                @setEvalBranchQuota(q);
                return 3;
            }
            """);
        cs.ShouldNotBeNull();
    }

    [Fact]
    public void A_comptime_int_const_is_read_by_a_const_local_and_an_array_extent()
    {
        var cs = EmitZig("""
            const n: comptime_int = 2 + 1;
            pub fn main() u8 {
                var a: [n]u8 = undefined;
                a[0] = 1;
                return a[0];
            }
            """);
        cs.ShouldNotBeNull();
    }

    [Fact]
    public void An_anytype_comptime_int_argument_keys_an_instance()
    {
        var cs = EmitZig("""
            fn id(comptime v: anytype) u8 { return v; }
            pub fn main() u8 {
                const a = 3;
                return id(a) + id(4);
            }
            """);
        cs.ShouldNotBeNull();
    }

    [Fact]
    public void A_comptime_repeat_count_and_a_byte_list_fold()
    {
        var cs = EmitZig("""
            const k = 2;
            pub fn main() u8 {
                const s = "ab" ** k;
                return @intCast(s.len);
            }
            """);
        cs.ShouldContain("abab");
    }
}
