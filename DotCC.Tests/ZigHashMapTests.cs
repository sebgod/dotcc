#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for what std.AutoHashMap from real std needed (road-to-zig-std, task #51): `errdefer comptime unreachable;`
/// dropped (zig's no-error-return assertion), an error union of an OPTIONAL (`!?usize`, `ErrUnion&lt;ulong?&gt;`), a
/// `packed struct` whose narrow fields are bit-fields (std.hash_map's one-byte `Metadata`), `@ptrCast` of a struct
/// pointer to a byte slice sized by the C# `sizeof` (std.mem.swap swapped NOTHING before, since a struct's
/// CType.SizeOf is 0), a slice passed where a `*const [N]T` is expected, `@bitCast` of an array to an integer, a
/// cast operand that starts with a unary operator, and `return @intCast(…)` in a `!T` function. End-to-end in the
/// <c>hash_map_shapes</c> zig-oracle program and the real-std std.AutoHashMap differential.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigHashMapTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zighm-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private const string Program = """
        const Meta = packed struct {
            fingerprint: u7 = 0,
            used: u1 = 0,
        };

        const Pair = struct { a: u64, b: u32 };

        fn swapBytes(comptime T: type, a: *T, b: *T) void {
            const a_bytes: []u8 = @ptrCast(a);
            const b_bytes: []u8 = @ptrCast(b);
            for (a_bytes, b_bytes) |*x, *y| {
                const t = x.*;
                x.* = y.*;
                y.* = t;
            }
        }

        fn readU32(bytes: *const [4]u8) u32 {
            return @bitCast(bytes.*);
        }

        fn wide(a: *const u64, b: *const u64) u128 {
            return @as(u128, a.*) * b.*;
        }

        fn find(xs: []const u8, x: u8) !?usize {
            for (xs, 0..) |v, i| {
                if (v == x) return i;
            }
            return null;
        }

        fn fill(out: []u8) !u8 {
            errdefer comptime unreachable;
            out[0] = 7;
            return @intCast(out.len);
        }

        pub fn main() !u8 {
            const m = Meta{ .fingerprint = 5, .used = 1 };
            const as_byte: u8 = @bitCast(m);
            var p = Pair{ .a = 1, .b = 2 };
            var q = Pair{ .a = 10, .b = 20 };
            swapBytes(Pair, &p, &q);
            const data = [_]u8{ 1, 0, 0, 0, 9, 9 };
            const r = readU32(data[0..4]);
            const x: u64 = 3;
            const y: u64 = 4;
            const w = wide(&x, &y);
            const idx = (try find(&data, 9)) orelse 99;
            var buf: [3]u8 = undefined;
            const n = try fill(&buf);
            return @intCast(as_byte - 133 + p.a + q.b + r + @as(u64, @intCast(w)) + idx + n + buf[0]);
        }
        """;

    [Fact]
    public void A_packed_struct_s_narrow_fields_share_one_byte()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("private byte __bf0;");
        cs.ShouldContain("public byte fingerprint { get => (byte)(((uint)(__bf0 >> 0) & 127u));");
        cs.ShouldContain("public byte used { get => (byte)(((uint)(__bf0 >> 7) & 1u));");
    }

    [Fact]
    public void A_struct_pointer_viewed_as_bytes_spans_the_struct_s_csharp_size()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("Slice<byte> a_bytes = new Slice<byte>((byte*)a, ((ulong)(sizeof(Pair))));");
    }

    [Fact]
    public void Slices_arrays_and_casts_reach_valid_csharp()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("uint r = readU32(new Slice<byte>(data + 0, unchecked((ulong)(4 - 0))).Ptr);");   // slice at a *const [4]u8
        cs.ShouldContain("return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<uint>(bytes);");       // @bitCast(bytes.*)
        cs.ShouldContain("return (System.UInt128)(*a) * *b;");                                              // not `(T)*a` (a product)
    }

    [Fact]
    public void Error_unions_of_optionals_and_cast_returns_lower_and_the_errdefer_assertion_is_dropped()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("internal static unsafe ErrUnion<ulong?> find(ConstSlice<byte> xs, byte x)");
        cs.ShouldContain("return ErrUnion<ulong?>.Ok(null);");
        cs.ShouldContain("return ErrUnion<byte>.Ok((byte)@out.Len);");
        // `errdefer comptime unreachable;` emits nothing: the body is the store and the return, no errdefer catch.
        cs.ShouldMatch(@"ErrUnion<byte> fill\(Slice<byte> @out\)\s*\{\s*try\s*\{\s*@out\.Ptr\[0\] = 7;\s*return ErrUnion<byte>\.Ok");
    }
}
