#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for forms std.hash's auto-hash path reaches (road-to-zig-std G4): an <c>if</c> statement whose then
/// arm is a bare <c>return x</c> before <c>else</c>; <c>inline</c> prongs and <c>|val, tag|</c> captures;
/// <c>.undefined</c> / <c>.null</c> enum literals; <c>@call</c>; <c>@divExact</c> (a folded quotient sizes an
/// array); <c>&amp;arr</c> as its <c>*[N]T</c> (the array itself, not a pointer to the element pointer); a
/// plain value at an error-union argument; the curated <c>std.mem.asBytes</c>; a comptime type question
/// (<c>comptime isSlice(T)</c>, <c>hasUniqueRepresentation</c>-shaped) folding so the branch it guards is never
/// analysed; and a lazily declared method keeping the calling body's container scope. End-to-end in the
/// <c>hash_path_forms</c> and multi-file <c>lazy_method_container_scope</c> zig-oracle programs.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigHashPathTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zighp-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void An_if_with_a_bare_return_then_arm_parses_plain_and_with_captures()
    {
        var cs = EmitZig("""
            fn pick(c: bool) u8 {
                if (c) return 30 else return 1;
            }
            fn unwrap(v: anyerror!u8) u8 {
                if (v) |x| return x else |_| return 0;
            }
            pub fn main() u8 {
                return pick(true) + unwrap(12);
            }
            """);
        cs.ShouldMatch(@"if \(Cond\.B\(c\)\)\s*\{?\s*return 30;");
        // The plain value at an `anyerror!u8` parameter is its success variant.
        cs.ShouldContain("unwrap(ErrUnion<byte>.Ok(12))");
    }

    [Fact]
    public void Inline_prongs_and_keyword_enum_literals_parse_and_fold()
    {
        var cs = EmitZig("""
            fn kind(comptime T: type) u8 {
                return switch (@typeInfo(T)) {
                    .undefined, .null => 0,
                    .int => 3,
                    inline else => 7,
                };
            }
            pub fn main() u8 {
                return kind(u32) + 39;
            }
            """);
        cs.ShouldMatch(@"byte kind__u32\(\)\s*\{\s*return 3;");
    }

    [Fact]
    public void Call_divExact_and_address_of_an_array_lower_as_zig_means()
    {
        var cs = EmitZig("""
            fn add(a: u8, b: u8) u8 { return a + b; }
            pub fn main() u8 {
                var buf: [@divExact(8, 4)]u8 = undefined;
                buf[0] = 40;
                buf[1] = 2;
                const p = &buf;
                const tail = p[1..];
                return @call(.auto, add, .{ buf[0], tail[0] });
            }
            """);
        cs.ShouldContain("stackalloc byte[2]");
        cs.ShouldContain("byte* p = buf;");
        cs.ShouldContain("add(buf[0], tail.Ptr[0])");
    }

    [Fact]
    public void The_curated_asBytes_is_a_byte_slice_over_the_item()
    {
        var cs = EmitZig("""
            const std = @import("std");
            pub fn main() u8 {
                var key: u32 = 0x2a;
                _ = &key;
                const bytes = std.mem.asBytes(&key);
                return bytes[0];
            }
            """);
        cs.ShouldContain("new Slice<byte>((byte*)&key, 4UL)");
    }

    [Fact]
    public void A_comptime_type_question_folds_so_its_guarded_compile_error_is_never_analysed()
    {
        var cs = EmitZig("""
            fn isSlice(comptime T: type) bool {
                return switch (@typeInfo(T)) {
                    .pointer => |info| info.size == .slice,
                    else => false,
                };
            }
            fn unique(comptime T: type) bool {
                return switch (@typeInfo(T)) {
                    .int => |info| @sizeOf(T) * 8 == info.bits,
                    else => false,
                };
            }
            pub fn main() u8 {
                if (comptime isSlice(u32)) @compileError("u32 is no slice");
                if (unique(u32)) return 42 else @compileError("u32 has a unique representation");
            }
            """);
        cs.ShouldContain("return 42;");
        cs.ShouldNotContain("no slice");
    }
}
