#nullable enable

using DotCC.Libc;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Stage-0 runtime gate for the Zig saturating operators (Milestone P, part 2): drives
/// <c>DotCC.Libc/ZigMath.cs</c> DIRECTLY (no lowering), proving the generic clamp helpers are
/// sound before any emit work — that the <c>IBinaryInteger</c>+<c>IMinMaxValue</c> generic math
/// instantiates over every integer width, the 128-bit accumulator clamps both directions for
/// signed and unsigned, the unsigned-subtraction floor is <c>0</c> (not a wrapped max), and the
/// signed <c>MinValue * -1</c> overflow edge saturates to the max instead of trapping.
/// </summary>
public sealed class ZigMathRuntimeTests
{
    // ---- unsigned add: clamp to MaxValue on overflow ----

    [Fact]
    public void SatAdd_u8_saturates_to_255_on_overflow()
    {
        ZigMath.SatAdd<byte>(200, 100).ShouldBe((byte)255);
        ZigMath.SatAdd<byte>(40, 2).ShouldBe((byte)42);     // no saturation
        ZigMath.SatAdd<byte>(255, 255).ShouldBe((byte)255);
    }

    [Fact]
    public void SatAdd_u64_saturates_to_max_on_overflow()
    {
        ZigMath.SatAdd<ulong>(ulong.MaxValue, 1).ShouldBe(ulong.MaxValue);
        ZigMath.SatAdd<ulong>(ulong.MaxValue, ulong.MaxValue).ShouldBe(ulong.MaxValue);
        ZigMath.SatAdd<ulong>(10, 32).ShouldBe(42UL);
    }

    // ---- unsigned sub: floor at 0 (NOT a wrapped huge value) ----

    [Fact]
    public void SatSub_unsigned_floors_at_zero()
    {
        ZigMath.SatSub<byte>(5, 10).ShouldBe((byte)0);
        ZigMath.SatSub<byte>(200, 50).ShouldBe((byte)150);
        ZigMath.SatSub<ulong>(0, ulong.MaxValue).ShouldBe(0UL);
    }

    // ---- unsigned mul: clamp to MaxValue ----

    [Fact]
    public void SatMul_unsigned_saturates_to_max()
    {
        ZigMath.SatMul<byte>(100, 100).ShouldBe((byte)255);
        ZigMath.SatMul<byte>(6, 7).ShouldBe((byte)42);      // no saturation
        ZigMath.SatMul<ulong>(ulong.MaxValue, 2).ShouldBe(ulong.MaxValue);
    }

    // ---- signed: clamp at both ends ----

    [Fact]
    public void SatAdd_signed_clamps_both_directions()
    {
        ZigMath.SatAdd<sbyte>(100, 100).ShouldBe((sbyte)127);
        ZigMath.SatAdd<sbyte>(-100, -100).ShouldBe((sbyte)-128);
        ZigMath.SatAdd<sbyte>(20, 22).ShouldBe((sbyte)42);  // no saturation
    }

    [Fact]
    public void SatSub_signed_clamps_both_directions()
    {
        ZigMath.SatSub<sbyte>(-100, 100).ShouldBe((sbyte)-128);
        ZigMath.SatSub<sbyte>(100, -100).ShouldBe((sbyte)127);
        ZigMath.SatSub<int>(int.MinValue, 1).ShouldBe(int.MinValue);
        ZigMath.SatSub<int>(int.MaxValue, -1).ShouldBe(int.MaxValue);
    }

    [Fact]
    public void SatMul_signed_clamps_and_survives_the_minvalue_negate_edge()
    {
        ZigMath.SatMul<sbyte>(100, 100).ShouldBe((sbyte)127);
        ZigMath.SatMul<sbyte>(-100, 100).ShouldBe((sbyte)-128);
        // MinValue * -1 overflows the type (the answer is +2^(n-1)); it must saturate to MaxValue,
        // NOT trap on a `MinValue / -1` division (the trap a naive divide-based check would hit).
        ZigMath.SatMul<sbyte>(sbyte.MinValue, -1).ShouldBe(sbyte.MaxValue);
        ZigMath.SatMul<int>(int.MinValue, -1).ShouldBe(int.MaxValue);
        ZigMath.SatMul<long>(long.MinValue, -1).ShouldBe(long.MaxValue);
        ZigMath.SatMul<sbyte>(7, 6).ShouldBe((sbyte)42); // no saturation
    }
}
