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

    // ---- overflow-detecting arithmetic: (wrapped, 0/1 flag) — Zig's `struct { T, u1 }` ----

    [Fact]
    public void AddWithOverflow_reports_the_wrapped_result_and_flag()
    {
        var (r, o) = ZigMath.AddWithOverflow<byte>(200, 100);
        r.ShouldBe((byte)44); o.ShouldBe((byte)1);              // 300 wraps to 44
        var (r2, o2) = ZigMath.AddWithOverflow<byte>(5, 10);
        r2.ShouldBe((byte)15); o2.ShouldBe((byte)0);            // no overflow
        var (rs, os) = ZigMath.AddWithOverflow<sbyte>(100, 100);
        rs.ShouldBe((sbyte)-56); os.ShouldBe((byte)1);          // 200 wraps to -56 (signed)
    }

    [Fact]
    public void SubWithOverflow_uses_the_signed_accumulator_for_an_unsigned_borrow()
    {
        var (r, o) = ZigMath.SubWithOverflow<byte>(5, 10);
        r.ShouldBe((byte)251); o.ShouldBe((byte)1);             // -5 wraps to 251
        var (r2, o2) = ZigMath.SubWithOverflow<byte>(200, 100);
        r2.ShouldBe((byte)100); o2.ShouldBe((byte)0);
        var (rs, os) = ZigMath.SubWithOverflow<sbyte>(-100, 100);
        rs.ShouldBe((sbyte)56); os.ShouldBe((byte)1);           // -200 wraps to 56
    }

    [Fact]
    public void MulWithOverflow_reports_the_low_bits_and_flag()
    {
        var (r, o) = ZigMath.MulWithOverflow<byte>(20, 20);
        r.ShouldBe((byte)144); o.ShouldBe((byte)1);             // 400 & 0xFF
        var (r2, o2) = ZigMath.MulWithOverflow<byte>(6, 7);
        r2.ShouldBe((byte)42); o2.ShouldBe((byte)0);
        // usize (u64): the 128-bit accumulator still captures the true product exactly.
        var (ru, ou) = ZigMath.MulWithOverflow<ulong>(ulong.MaxValue, 2);
        ru.ShouldBe(ulong.MaxValue - 1); ou.ShouldBe((byte)1);
    }

    [Fact]
    public void ShlWithOverflow_sets_the_flag_when_high_bits_are_lost()
    {
        var (r, o) = ZigMath.ShlWithOverflow<byte>(1, 7);
        r.ShouldBe((byte)128); o.ShouldBe((byte)0);             // 1<<7 = 128 fits in u8
        var (r2, o2) = ZigMath.ShlWithOverflow<byte>(3, 7);
        r2.ShouldBe((byte)128); o2.ShouldBe((byte)1);           // 384 -> 128, a set bit shifted out
    }
}
