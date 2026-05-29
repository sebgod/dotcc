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

    [Theory]
    [InlineData(2.0, 3.0, 6.0)]
    [InlineData(-2.0, 3.0, -6.0)]
    [InlineData(-2.0, -3.0, 6.0)]
    [InlineData(0.5, 0.5, 0.25)]
    [InlineData(1.5, 1.5, 2.25)]
    [InlineData(1.25, 1.25, 1.5625)]      // all exact in binary
    [InlineData(2e10, 3e10, 6e20)]        // exact integer product, narrows back exactly
    [InlineData(123.0, 0.0, 0.0)]
    public void Multiply_basic_cases_round_trip_through_double(double a, double b, double expected)
    {
        Float128 p = Float128.Multiply(Float128.FromDouble(a), Float128.FromDouble(b));
        Float128.ToDouble(p).ShouldBe(expected);
    }

    [Fact]
    public void Multiply_special_values_follow_ieee()
    {
        var inf = Float128.PositiveInfinity;
        Float128.IsNaN(Float128.Multiply(inf, Float128.Zero)).ShouldBeTrue();   // inf * 0 = NaN
        Float128.IsInfinity(Float128.Multiply(inf, Float128.FromDouble(2.0))).ShouldBeTrue();
        Float128.IsNaN(Float128.Multiply(Float128.NaN, Float128.One)).ShouldBeTrue();
        // Sign of zero from sign of factors.
        Float128.Multiply(Float128.FromDouble(-1.0), Float128.Zero).Bits.ShouldBe(Float128.NegativeZero.Bits);
        Float128.Multiply(Float128.FromDouble(-1.0), Float128.NegativeZero).Bits.ShouldBe(Float128.Zero.Bits);
    }

    [Fact]
    public void Operators_and_conversions_behave_like_a_numeric_type()
    {
        // Implicit int/long widening (the `Float128 x = 3;` shape the emitter
        // produces for C integer literals), operators, explicit narrowing.
        Float128 a = 3;          // int → long → Float128 (implicit)
        Float128 b = 4;
        ((int)(a * a + b * b)).ShouldBe(25);   // 9 + 16
        ((int)(a - b)).ShouldBe(-1);
        ((int)(-a)).ShouldBe(-3);

        Float128 d = 2.5;        // double widening (implicit)
        ((double)d).ShouldBe(2.5);
        ((double)(d + d)).ShouldBe(5.0);

        // long round-trip and truncation toward zero.
        ((long)(Float128)9_000_000_000L).ShouldBe(9_000_000_000L);
        ((long)(Float128)2.9).ShouldBe(2L);
        ((long)(Float128)(-2.9)).ShouldBe(-2L);
    }

    [Fact]
    public void Integer_conversions_handle_edges()
    {
        ((long)Float128.FromInt64(long.MaxValue)).ShouldBe(long.MaxValue);
        ((long)Float128.FromInt64(long.MinValue)).ShouldBe(long.MinValue);
        Float128.ToInt64(Float128.NaN).ShouldBe(0L);
        Float128.ToInt64(Float128.PositiveInfinity).ShouldBe(long.MaxValue);
        Float128.ToInt64(Float128.NegativeInfinity).ShouldBe(long.MinValue);
    }

    [Theory]
    [InlineData(6.0, 2.0, 3.0)]
    [InlineData(1.0, 4.0, 0.25)]
    [InlineData(-9.0, 3.0, -3.0)]
    [InlineData(7.0, 2.0, 3.5)]
    public void Divide_basic_cases(double a, double b, double expected)
        => Float128.ToDouble(Float128.Divide(Float128.FromDouble(a), Float128.FromDouble(b))).ShouldBe(expected);

    [Fact]
    public void Divide_special_values_follow_ieee()
    {
        Float128.IsInfinity(Float128.Divide(Float128.One, Float128.Zero)).ShouldBeTrue();   // 1/0 = inf
        Float128.IsNaN(Float128.Divide(Float128.Zero, Float128.Zero)).ShouldBeTrue();         // 0/0 = NaN
        Float128.IsNaN(Float128.Divide(Float128.PositiveInfinity, Float128.PositiveInfinity)).ShouldBeTrue();
        Float128.ToDouble(Float128.Divide(Float128.FromDouble(-1.0), Float128.Zero)).ShouldBe(double.NegativeInfinity);
    }

    [Theory]
    [InlineData(4.0, 2.0)]
    [InlineData(9.0, 3.0)]
    [InlineData(2.0, 1.4142135623730951)]   // sqrt(2) narrowed to double
    [InlineData(0.25, 0.5)]
    [InlineData(1e300, 1e150)]
    public void Sqrt_basic_cases(double x, double expected)
        => Float128.ToDouble(Float128.Sqrt(Float128.FromDouble(x))).ShouldBe(expected);

    [Fact]
    public void Sqrt_special_values()
    {
        Float128.Sqrt(Float128.Zero).Bits.ShouldBe(Float128.Zero.Bits);
        Float128.Sqrt(Float128.NegativeZero).Bits.ShouldBe(Float128.NegativeZero.Bits); // sqrt(-0) = -0
        Float128.IsNaN(Float128.Sqrt(Float128.FromDouble(-1.0))).ShouldBeTrue();
        Float128.IsInfinity(Float128.Sqrt(Float128.PositiveInfinity)).ShouldBeTrue();
    }

    [Fact]
    public void Fma_is_single_rounding()
    {
        // 2*3 + 4 = 10.
        Float128.ToDouble(Float128.FusedMultiplyAdd(
            Float128.FromDouble(2.0), Float128.FromDouble(3.0), Float128.FromDouble(4.0))).ShouldBe(10.0);
        // The product is exact (no intermediate rounding) before the add.
        Float128 a = Float128.FromInt64(3), b = Float128.FromInt64(5), c = Float128.FromInt64(-15);
        Float128.IsZero(Float128.FusedMultiplyAdd(a, b, c)).ShouldBeTrue(); // 15 - 15 = 0
        Float128.IsNaN(Float128.FusedMultiplyAdd(Float128.Zero, Float128.PositiveInfinity, Float128.One)).ShouldBeTrue();
    }

    [Fact]
    public void Fixed_formatting_matches_printf_f()
    {
        Float128.FromDouble(1.5).ToFixedString(3).ShouldBe("1.500");
        Float128.One.ToFixedString(2).ShouldBe("1.00");
        Float128.FromInt64(-25).ToFixedString(0).ShouldBe("-25");
        Float128.FromDouble(1234.5).ToFixedString(1).ShouldBe("1234.5");
        Float128.Zero.ToFixedString(2).ShouldBe("0.00");
        Float128.FromDouble(0.5).ToFixedString(0).ShouldBe("0");      // round-half-to-even → 0
        Float128.FromDouble(1.5).ToFixedString(0).ShouldBe("2");      // ties-to-even → 2
        Float128.IsNaN(Float128.NaN) .ShouldBeTrue();
        Float128.NaN.ToFixedString(2).ShouldBe("nan");
        Float128.NegativeInfinity.ToFixedString(2).ShouldBe("-inf");
    }

    [Fact]
    public void Scientific_and_general_formatting()
    {
        Float128.FromDouble(0.5).ToScientificString(2, upper: false).ShouldBe("5.00e-01");
        Float128.FromDouble(1234.5).ToScientificString(4, upper: false).ShouldBe("1.2345e+03");
        Float128.FromInt64(100000).ToGeneralString(3, upper: false).ShouldBe("1e+05");
        Float128.FromDouble(1234.5).ToGeneralString(6, upper: false).ShouldBe("1234.5");
        Float128.FromDouble(0.0001).ToGeneralString(6, upper: false).ShouldBe("0.0001");
    }

    [Theory]
    [InlineData(2.7, 2.0, 3.0, 2.0, 3.0)]      // floor, ceil, trunc, round
    [InlineData(-2.7, -3.0, -2.0, -2.0, -3.0)]
    [InlineData(2.5, 2.0, 3.0, 2.0, 2.0)]      // round ties-to-even → 2
    [InlineData(3.5, 3.0, 4.0, 3.0, 4.0)]      // ties-to-even → 4
    [InlineData(5.0, 5.0, 5.0, 5.0, 5.0)]      // integral
    public void Integral_functions(double x, double floor, double ceil, double trunc, double round)
    {
        Float128 v = Float128.FromDouble(x);
        Float128.ToDouble(Float128.Floor(v)).ShouldBe(floor);
        Float128.ToDouble(Float128.Ceiling(v)).ShouldBe(ceil);
        Float128.ToDouble(Float128.Truncate(v)).ShouldBe(trunc);
        Float128.ToDouble(Float128.Round(v)).ShouldBe(round);
    }

    [Theory]
    [InlineData(8.0, 2.0)]
    [InlineData(27.0, 3.0)]
    [InlineData(-8.0, -2.0)]     // cbrt preserves sign
    [InlineData(0.125, 0.5)]
    [InlineData(1000.0, 10.0)]
    public void Cbrt_basic_cases(double x, double expected)
        => Float128.ToDouble(Float128.Cbrt(Float128.FromDouble(x))).ShouldBe(expected, 1e-13);

    [Theory]
    [InlineData(3.0, 4.0, 5.0)]
    [InlineData(5.0, 12.0, 13.0)]
    [InlineData(0.0, 7.0, 7.0)]
    public void Hypot_basic_cases(double x, double y, double expected)
        => Float128.ToDouble(Float128.Hypot(Float128.FromDouble(x), Float128.FromDouble(y))).ShouldBe(expected, 1e-13);

    [Fact]
    public void Cbrt_and_Hypot_special_values()
    {
        Float128.Cbrt(Float128.NegativeZero).Bits.ShouldBe(Float128.NegativeZero.Bits);
        // cbrt(-inf) = -inf (infinite and negative).
        Float128.IsInfinity(Float128.Cbrt(Float128.NegativeInfinity)).ShouldBeTrue();
        Float128.IsNegative(Float128.Cbrt(Float128.NegativeInfinity)).ShouldBeTrue();
        Float128.IsInfinity(Float128.Hypot(Float128.PositiveInfinity, Float128.NaN)).ShouldBeTrue(); // hypot(inf,nan)=inf
        Float128.IsNaN(Float128.Hypot(Float128.NaN, Float128.One)).ShouldBeTrue();
    }

    [Fact]
    public void CopySign_and_Fmod()
    {
        Float128.ToDouble(Float128.CopySign(Float128.FromDouble(3.0), Float128.FromDouble(-1.0))).ShouldBe(-3.0);
        Float128.ToDouble(Float128.CopySign(Float128.FromDouble(-3.0), Float128.FromDouble(1.0))).ShouldBe(3.0);
        Float128.ToDouble(Float128.Fmod(Float128.FromDouble(7.5), Float128.FromDouble(2.0))).ShouldBe(1.5);
        Float128.ToDouble(Float128.Fmod(Float128.FromDouble(-7.5), Float128.FromDouble(2.0))).ShouldBe(-1.5);
        Float128.IsNaN(Float128.Fmod(Float128.FromDouble(1.0), Float128.Zero)).ShouldBeTrue();
    }

    [Fact]
    public void Relational_operators_follow_ieee()
    {
        Float128 one = Float128.One, two = Float128.FromInt64(2);
        (one < two).ShouldBeTrue();
        (two < one).ShouldBeFalse();
        (one <= one).ShouldBeTrue();
        (two > one).ShouldBeTrue();
        (Float128.Negate(two) < Float128.Negate(one)).ShouldBeTrue(); // -2 < -1
        (Float128.NegativeInfinity < Float128.PositiveInfinity).ShouldBeTrue();
        // NaN is unordered: every relational operator is false.
        (Float128.NaN < one).ShouldBeFalse();
        (Float128.NaN >= one).ShouldBeFalse();
        (one < Float128.NaN).ShouldBeFalse();
    }
}
