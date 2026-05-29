#nullable enable

using System;
using DotCC.Libc;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Fast, in-process unit tests for our MIT <see cref="Float128"/> (IEEE
/// binary128) — Stage 1a: representation, constants, predicates, exact
/// <c>double</c> widening, correctly-rounded narrowing, comparison. No
/// subprocess: the gcc/long-double differential oracle (for arithmetic) lives
/// in DotCC.FunctionalTests. Rounding-tie cases here are hand-computed.
/// </summary>
public sealed class Float128Tests
{
    // binary128 of 1.0: biased exponent 16383 (0x3FFF), zero fraction.
    private static readonly UInt128 OneBits = (UInt128)0x3FFF << 112;

    [Fact]
    public void One_has_the_expected_bit_pattern()
    {
        Float128.FromDouble(1.0).Bits.ShouldBe(OneBits);
        Float128.One.Bits.ShouldBe(OneBits);
    }

    [Fact]
    public void Predicates_classify_special_values()
    {
        Float128.IsNaN(Float128.NaN).ShouldBeTrue();
        Float128.IsNaN(Float128.PositiveInfinity).ShouldBeFalse();
        Float128.IsInfinity(Float128.PositiveInfinity).ShouldBeTrue();
        Float128.IsInfinity(Float128.NegativeInfinity).ShouldBeTrue();
        Float128.IsFinite(Float128.One).ShouldBeTrue();
        Float128.IsFinite(Float128.NaN).ShouldBeFalse();
        Float128.IsZero(Float128.Zero).ShouldBeTrue();
        Float128.IsZero(Float128.NegativeZero).ShouldBeTrue();
        Float128.IsNegative(Float128.NegativeZero).ShouldBeTrue();
        Float128.IsNegative(Float128.NegativeInfinity).ShouldBeTrue();
        Float128.IsNegative(Float128.One).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(-2.5)]
    [InlineData(0.1)]                 // not exact in binary, full 52-bit mantissa
    [InlineData(3.141592653589793)]
    [InlineData(1.4142135623730951)]  // sqrt(2) as a double
    [InlineData(123456789.0)]
    [InlineData(double.MaxValue)]     // QuadrupleLib 0.1.6 got this wrong
    [InlineData(double.MinValue)]
    [InlineData(double.Epsilon)]      // smallest subnormal double — QL got this wrong
    [InlineData(2.2250738585072014e-308)] // smallest normal double
    public void Double_widens_and_narrows_exactly(double d)
    {
        // double → binary128 is always exact; binary128 → double must return
        // the identical double. Exercises normal + subnormal + Max paths.
        Float128.ToDouble(Float128.FromDouble(d)).ShouldBe(d);
    }

    [Fact]
    public void Infinities_and_nan_survive_the_double_roundtrip()
    {
        double.IsPositiveInfinity(Float128.ToDouble(Float128.FromDouble(double.PositiveInfinity))).ShouldBeTrue();
        double.IsNegativeInfinity(Float128.ToDouble(Float128.FromDouble(double.NegativeInfinity))).ShouldBeTrue();
        double.IsNaN(Float128.ToDouble(Float128.FromDouble(double.NaN))).ShouldBeTrue();
    }

    [Fact]
    public void Narrowing_rounds_half_to_even_down()
    {
        // 1.0 + 2^-53 is exactly halfway between 1.0 (fraction LSB 0, "even")
        // and the next double. Ties-to-even ⇒ 1.0.
        Float128 halfway = Float128.FromBits(OneBits | (UInt128.One << (112 - 53)));
        Float128.ToDouble(halfway).ShouldBe(1.0);
    }

    [Fact]
    public void Narrowing_rounds_half_to_even_up()
    {
        // 1.0 + 2^-53 + 2^-100: just past halfway ⇒ rounds up to the next
        // double after 1.0 (== 1.0 + 2^-52).
        Float128 justOver = Float128.FromBits(
            OneBits | (UInt128.One << (112 - 53)) | (UInt128.One << (112 - 100)));
        Float128.ToDouble(justOver).ShouldBe(BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001UL));
    }

    [Fact]
    public void Narrowing_above_double_range_overflows_to_infinity()
    {
        // Exponent well beyond double's max (unbiased 2000) ⇒ ±inf.
        Float128 huge = Float128.FromBits((UInt128)(2000 + 16383) << 112);
        double.IsPositiveInfinity(Float128.ToDouble(huge)).ShouldBeTrue();
        double.IsNegativeInfinity(Float128.ToDouble(Float128.Negate(huge))).ShouldBeTrue();
    }

    [Fact]
    public void Comparison_follows_ieee_semantics()
    {
        // NaN is unordered: never equal, even to itself.
        (Float128.NaN == Float128.NaN).ShouldBeFalse();
        (Float128.NaN != Float128.NaN).ShouldBeTrue();
        // +0 == -0.
        (Float128.Zero == Float128.NegativeZero).ShouldBeTrue();
        // Distinct finite values.
        (Float128.One == Float128.FromDouble(1.0)).ShouldBeTrue();
        (Float128.One == Float128.FromDouble(2.0)).ShouldBeFalse();
    }

    [Fact]
    public void Negate_and_abs_flip_only_the_sign()
    {
        Float128 x = Float128.FromDouble(3.5);
        Float128.ToDouble(Float128.Negate(x)).ShouldBe(-3.5);
        Float128.ToDouble(Float128.Abs(Float128.Negate(x))).ShouldBe(3.5);
        Float128.ToDouble(Float128.Abs(x)).ShouldBe(3.5);
    }

    [Theory]
    [InlineData(1.0, 1.0, 2.0)]
    [InlineData(1.0, 2.0, 3.0)]
    [InlineData(0.5, 0.5, 1.0)]
    [InlineData(0.1, 0.2, 0.30000000000000004)] // matches binary64's 0.1+0.2 once narrowed
    [InlineData(-2.5, 1.5, -1.0)]
    [InlineData(1e18, 1.0, 1e18)] // +1 is far below half a ulp at 1e18 → narrows back to 1e18
    [InlineData(3.0, -3.0, 0.0)]
    [InlineData(1234.5, -1000.25, 234.25)]
    public void Add_basic_cases_round_trip_through_double(double a, double b, double expected)
    {
        // Operands chosen so the binary128 sum narrows back to the expected
        // double exactly (full validation vs gcc is the oracle's job).
        Float128 sum = Float128.Add(Float128.FromDouble(a), Float128.FromDouble(b));
        Float128.ToDouble(sum).ShouldBe(expected);
    }

    [Fact]
    public void Add_special_values_follow_ieee()
    {
        var inf = Float128.PositiveInfinity;
        var ninf = Float128.NegativeInfinity;
        Float128.IsInfinity(Float128.Add(inf, Float128.One)).ShouldBeTrue();
        Float128.IsNaN(Float128.Add(inf, ninf)).ShouldBeTrue();   // inf + -inf = NaN
        Float128.IsNaN(Float128.Add(Float128.NaN, Float128.One)).ShouldBeTrue();
        // -0 + -0 = -0; +0 + -0 = +0.
        Float128.Add(Float128.NegativeZero, Float128.NegativeZero).Bits.ShouldBe(Float128.NegativeZero.Bits);
        Float128.Add(Float128.Zero, Float128.NegativeZero).Bits.ShouldBe(Float128.Zero.Bits);
        // x + (-x) = +0.
        Float128.Add(Float128.One, Float128.Negate(Float128.One)).Bits.ShouldBe(Float128.Zero.Bits);
    }

    [Fact]
    public void Subtract_is_add_of_negation()
    {
        Float128 r = Float128.Subtract(Float128.FromDouble(10.0), Float128.FromDouble(3.5));
        Float128.ToDouble(r).ShouldBe(6.5);
    }
}
