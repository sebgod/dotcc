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
}
