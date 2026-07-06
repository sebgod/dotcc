#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for curated <c>std.debug.print</c> (wall-plan W6) — the biggest remaining <c>std</c> idiom,
/// and the last brick of the monomorphization arc. Like real Zig, <c>std.debug.print("{d}\n", .{n})</c>
/// writes to STDERR: it lowers to <c>fprintf(stderr, "&lt;C-fmt&gt;").Arg(…).Done()</c> over dotcc's
/// existing printf-builder. The comptime format string is parsed AT LOWERING TIME (no reflection) and its
/// <c>{…}</c> placeholders paired positionally with the tuple elements, each translated to the equivalent
/// C conversion (<c>{d}</c>/<c>{}</c> → <c>%d</c>, <c>{s}</c> → <c>%s</c>, <c>{c}</c> → <c>%c</c>,
/// <c>{x}</c> → <c>%x</c>); <c>{{</c>/<c>}}</c> fold to literal braces and a literal <c>%</c> is doubled.
/// V1 cuts (loud): a float / bool / slice argument, a width/named specifier, <c>{any}</c>, a non-literal
/// format, and any non-<c>print</c> <c>std.debug</c> member. End-to-end in the <c>debug-print</c>
/// zig-oracle program (which asserts against real zig's stderr).
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigDebugPrintTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigdbg-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private const string StdImport = "const std = @import(\"std\");\n";

    [Fact]
    public void Print_lowers_to_an_fprintf_stderr_fluent_chain()
    {
        // std.debug.print → fprintf(stderr, …).Arg(…).Done() — the existing printf-builder path, but
        // targeting stderr (WriterFor(stderr) == Console.Error), exactly as real Zig's std.debug.print.
        var cs = EmitZig(StdImport + """
            pub fn main() void {
                const n: i32 = 42;
                std.debug.print("n={d}\n", .{n});
            }
            """);
        cs.ShouldContain("fprintf(stderr, ");     // stderr stream, not stdout
        cs.ShouldContain("%d");                     // {d} → %d
        cs.ShouldContain(".Arg(");                  // the argument rides the fluent builder
        cs.ShouldContain(".Done()");
    }

    [Fact]
    public void Placeholders_translate_to_c_printf_conversions()
    {
        // {d}/{s}/{c}/{x} → %d/%s/%c/%x (the runtime builder keys off the .Arg overload, so no length
        // modifier is needed — {d} on an i64 is still %d and prints the full 64-bit value).
        var cs = EmitZig(StdImport + """
            pub fn main() void {
                std.debug.print("d={d} s={s} c={c} x={x}\n", .{ 42, "hi", 65, 255 });
            }
            """);
        cs.ShouldContain("d=%d s=%s c=%c x=%x\\n");
    }

    [Fact]
    public void Braces_and_percent_are_escaped_for_printf()
    {
        // {{ / }} fold to literal braces; a literal % is doubled to %% (printf-escaped, so the runtime
        // builder folds it back to a single %).
        var cs = EmitZig(StdImport + """
            pub fn main() void {
                std.debug.print("{{literal}} 50% done\n", .{});
            }
            """);
        cs.ShouldContain("{literal} 50%% done\\n");
    }

    [Fact]
    public void Empty_argument_tuple_emits_no_Arg()
    {
        // std.debug.print("…", .{}) — a bare format with no arguments: fprintf(stderr, …).Done() with
        // no `.Arg(…)` between them. (Pin the exact chain rather than a global `.Arg(` absence — the
        // spliced Libc runtime block naturally contains `.Arg(` elsewhere.)
        var cs = EmitZig(StdImport + """
            pub fn main() void {
                std.debug.print("hello\n", .{});
            }
            """);
        cs.ShouldContain("fprintf(stderr, Libc.L(\"hello\\n\\0\"u8)).Done()");
    }

    [Fact]
    public void Argument_count_mismatch_is_rejected()
    {
        // Two placeholders, one argument — real Zig errors at comptime; dotcc rejects it at lowering.
        var ex = Should.Throw<Exception>(() => EmitZig(StdImport + """
            pub fn main() void {
                std.debug.print("{d} {d}\n", .{42});
            }
            """));
        ex.Message.ShouldContain("placeholder");
    }

    [Fact]
    public void Unsupported_placeholder_is_rejected()
    {
        // A width/alignment specifier ({d:0>5}) is a V1 cut.
        var ex = Should.Throw<Exception>(() => EmitZig(StdImport + """
            pub fn main() void {
                std.debug.print("{d:0>5}\n", .{42});
            }
            """));
        ex.Message.ShouldContain("not supported");
    }

    [Fact]
    public void Float_argument_is_rejected()
    {
        // Zig's {d} on a float prints decimal in a way C's %f can't match byte-for-byte, so a float
        // argument is a loud cut in V1 (use printf for floats).
        var ex = Should.Throw<Exception>(() => EmitZig(StdImport + """
            pub fn main() void {
                const f: f64 = 1.5;
                std.debug.print("{d}\n", .{f});
            }
            """));
        ex.Message.ShouldContain("integer");
    }

    [Fact]
    public void String_placeholder_on_a_slice_is_rejected()
    {
        // Zig's {s} on a slice prints exactly .len bytes; C %s reads to a NUL — they can diverge, so a
        // slice {s} is a V1 cut (a NUL-terminated string pointer / literal is fine).
        var ex = Should.Throw<Exception>(() => EmitZig(StdImport + """
            pub fn main() void {
                const s: []const u8 = "hi";
                std.debug.print("{s}\n", .{s});
            }
            """));
        ex.Message.ShouldContain("{s}");
    }

    [Fact]
    public void Non_print_std_debug_member_is_rejected()
    {
        // std.debug is a curated path — only `print` is modeled; any other member is a clear cut.
        var ex = Should.Throw<Exception>(() => EmitZig(StdImport + """
            pub fn main() void {
                std.debug.assert(true);
            }
            """));
        ex.Message.ShouldContain("std.debug");
    }
}
