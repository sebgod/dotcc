#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for zig SIMD vectors (road-to-zig-std, the target-identity segment T5): <c>@Vector(N, T)</c> lowers to
/// .NET's <c>System.Runtime.Intrinsics.Vector64/128/256/512&lt;T&gt;</c> by total width, a bool vector (what a
/// comparison yields) to a <c>ulong</c> lane mask, and the operations route through <c>DotCC.Libc.ZigVec</c>.
/// End-to-end in the <c>simd_vectors</c> zig-oracle program and the real-std
/// <c>std.mem.indexOfScalar</c> differential, whose SIMD path is what these exist for.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigVectorTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigvec-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_vector_is_a_dotnet_vector_and_its_operations_are_element_wise()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                const V = @Vector(16, u8);
                const a: V = @splat(3);
                var arr: [16]u8 = undefined;
                for (&arr, 0..) |*e, i| e.* = @intCast(i);
                const b: V = arr;
                const c = a + b;
                return @reduce(.Max, c) + c[1];
            }
            """);
        cs.ShouldContain("System.Runtime.Intrinsics.Vector128<byte> a = System.Runtime.Intrinsics.Vector128.Create((byte)3);");
        cs.ShouldContain("System.Runtime.Intrinsics.Vector128<byte> b = ZigVec.Load128(arr);");   // an array at a vector sink
        cs.ShouldContain("System.Runtime.Intrinsics.Vector128<byte> c = a + b;");
        cs.ShouldContain("ZigVec.ReduceMax(c)");
        cs.ShouldContain("ZigVec.Get(c, 1)");
    }

    [Fact]
    public void A_comparison_is_a_lane_mask_that_reduce_and_select_read()
    {
        var cs = EmitZig("""
            pub fn main() u8 {
                const V = @Vector(8, u32);
                const a: V = @splat(5);
                const b: V = .{ 1, 5, 9, 5, 0, 0, 0, 0 };
                const m = a == b;
                const picked = @select(u32, m, a, @as(V, @splat(0)));
                var r: u8 = 0;
                if (@reduce(.Or, m)) r += 1;
                if (!@reduce(.And, m)) r += 2;
                return r + @as(u8, @intCast(@reduce(.Add, picked)));
            }
            """);
        cs.ShouldContain("System.Runtime.Intrinsics.Vector256<uint> a");
        // A list literal at a vector sink is one lane per element.
        cs.ShouldContain("Vector256<uint> b = System.Runtime.Intrinsics.Vector256.Create((uint)1, (uint)5, (uint)9, (uint)5, (uint)0, (uint)0, (uint)0, (uint)0);");
        cs.ShouldContain("ulong m = ZigVec.Eq(a, b);");
        cs.ShouldContain("ZigVec.Select(m, a,");
        cs.ShouldContain("m != 0UL");                    // `.Or` over a mask: any lane
        cs.ShouldContain("m == ZigVec.Full(8)");         // `.And`: every lane
        cs.ShouldContain("ZigVec.ReduceAdd(picked)");
    }

    [Fact]
    public void Type_info_reports_a_vector_with_its_length_and_child()
    {
        var cs = EmitZig("""
            fn lanes(comptime T: type) usize {
                return switch (@typeInfo(T)) {
                    .vector => |info| info.len,
                    else => 0,
                };
            }
            pub fn main() u8 {
                const V = @Vector(4, f32);
                const E = @typeInfo(V).vector.child;
                const x: E = 1.5;
                return @intCast(lanes(V) + @as(usize, @intFromFloat(x)));
            }
            """);
        cs.ShouldContain("float x = 1.5F;");   // `.child` is the lane type, f32
        cs.ShouldMatch(@"ulong lanes__v4_f32\(\)\s*\{\s*return 4;");
    }
}
