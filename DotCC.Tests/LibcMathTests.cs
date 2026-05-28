#nullable enable

using System;
using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the <c>&lt;math.h&gt;</c> / <c>&lt;tgmath.h&gt;</c> surface
/// in <see cref="DotCC.Libc.Libc"/>. Each function is exercised through both
/// its <c>double</c> overload (routes to <see cref="Math"/>) and its
/// <c>float</c> overload (routes to <see cref="MathF"/>) — the float pair
/// is what <c>tgmath.h</c> dispatches to in real C via <c>_Generic</c>; in
/// dotcc the same dispatch falls out of C# overload resolution.
/// </summary>
public sealed class LibcMathTests
{
    private const double Eps = 1e-12;
    private const float EpsF = 1e-6f;

    // -----------------------------------------------------------------
    // Trigonometric — sin / cos / tan + inverses + atan2
    // -----------------------------------------------------------------

    [Fact]
    public void sin_double_matches_Math_Sin() => sin(0.5).ShouldBe(Math.Sin(0.5), Eps);

    [Fact]
    public void sin_float_matches_MathF_Sin() => sin(0.5f).ShouldBe(MathF.Sin(0.5f), EpsF);

    [Fact]
    public void sinf_routes_to_MathF_Sin() => sinf(0.5f).ShouldBe(MathF.Sin(0.5f), EpsF);

    [Fact]
    public void cos_double_matches_Math_Cos() => cos(0.5).ShouldBe(Math.Cos(0.5), Eps);

    [Fact]
    public void cos_float_matches_MathF_Cos() => cos(0.5f).ShouldBe(MathF.Cos(0.5f), EpsF);

    [Fact]
    public void tan_double_matches_Math_Tan() => tan(0.3).ShouldBe(Math.Tan(0.3), Eps);

    [Fact]
    public void asin_double_inverse_of_sin() => asin(sin(0.5)).ShouldBe(0.5, Eps);

    [Fact]
    public void acos_double_inverse_of_cos() => acos(cos(0.5)).ShouldBe(0.5, Eps);

    [Fact]
    public void atan_double_matches_Math_Atan() => atan(1.0).ShouldBe(Math.PI / 4, Eps);

    [Fact]
    public void atan2_double_full_quadrant() => atan2(1.0, 1.0).ShouldBe(Math.PI / 4, Eps);

    [Fact]
    public void atan2_float_full_quadrant() =>
        atan2(1.0f, 1.0f).ShouldBe(MathF.PI / 4, EpsF);

    // -----------------------------------------------------------------
    // Hyperbolic
    // -----------------------------------------------------------------

    [Fact]
    public void sinh_double_matches_Math_Sinh() => sinh(1.0).ShouldBe(Math.Sinh(1.0), Eps);

    [Fact]
    public void cosh_double_matches_Math_Cosh() => cosh(1.0).ShouldBe(Math.Cosh(1.0), Eps);

    [Fact]
    public void tanh_float_matches_MathF_Tanh() => tanh(0.5f).ShouldBe(MathF.Tanh(0.5f), EpsF);

    // -----------------------------------------------------------------
    // Exponentials and logarithms
    // -----------------------------------------------------------------

    [Fact]
    public void exp_double_matches_Math_Exp() => exp(1.0).ShouldBe(Math.E, Eps);

    [Fact]
    public void log_double_natural_log_of_e_is_one() => log(Math.E).ShouldBe(1.0, Eps);

    [Fact]
    public void log10_double_log10_of_100_is_two() => log10(100.0).ShouldBe(2.0, Eps);

    [Fact]
    public void log2_double_log2_of_8_is_three() => log2(8.0).ShouldBe(3.0, Eps);

    // -----------------------------------------------------------------
    // Power and roots
    // -----------------------------------------------------------------

    [Fact]
    public void pow_double_2_pow_10_is_1024() => pow(2.0, 10.0).ShouldBe(1024.0, Eps);

    [Fact]
    public void pow_float_2_pow_10_is_1024() => pow(2.0f, 10.0f).ShouldBe(1024.0f, EpsF);

    [Fact]
    public void powf_routes_to_MathF_Pow() => powf(2.0f, 10.0f).ShouldBe(1024.0f, EpsF);

    [Fact]
    public void sqrt_double_sqrt_of_2() => sqrt(2.0).ShouldBe(M_SQRT2, Eps);

    [Fact]
    public void sqrt_float_sqrt_of_2() => sqrt(2.0f).ShouldBe(MathF.Sqrt(2.0f), EpsF);

    [Fact]
    public void cbrt_double_cube_root_of_27_is_3() => cbrt(27.0).ShouldBe(3.0, Eps);

    // -----------------------------------------------------------------
    // Rounding
    // -----------------------------------------------------------------

    [Fact]
    public void ceil_double_rounds_up() => ceil(3.2).ShouldBe(4.0);

    [Fact]
    public void floor_double_rounds_down() => floor(3.8).ShouldBe(3.0);

    [Fact]
    public void round_double_away_from_zero_on_tie() => round(0.5).ShouldBe(1.0);

    [Fact]
    public void round_double_negative_tie_away_from_zero() => round(-0.5).ShouldBe(-1.0);

    [Fact]
    public void trunc_double_toward_zero() => trunc(3.7).ShouldBe(3.0);

    [Fact]
    public void trunc_double_negative_toward_zero() => trunc(-3.7).ShouldBe(-3.0);

    // -----------------------------------------------------------------
    // Abs, mod, min, max
    // -----------------------------------------------------------------

    [Fact]
    public void fabs_double_negative_input() => fabs(-3.5).ShouldBe(3.5);

    [Fact]
    public void fabs_float_negative_input() => fabs(-3.5f).ShouldBe(3.5f);

    [Fact]
    public void fmod_double_positive_dividend() => fmod(7.5, 2.0).ShouldBe(1.5, Eps);

    [Fact]
    public void fmod_double_negative_dividend_sign_matches_x() =>
        // C99: result has the sign of the dividend (x). -7.5 mod 2 = -1.5.
        fmod(-7.5, 2.0).ShouldBe(-1.5, Eps);

    [Fact]
    public void fmin_double_picks_smaller() => fmin(3.0, 5.0).ShouldBe(3.0);

    [Fact]
    public void fmax_double_picks_larger() => fmax(3.0, 5.0).ShouldBe(5.0);

    // -----------------------------------------------------------------
    // Classification
    // -----------------------------------------------------------------

    [Fact]
    public void isnan_returns_1_for_nan() => isnan(double.NaN).ShouldBe(1);

    [Fact]
    public void isnan_returns_0_for_finite() => isnan(1.0).ShouldBe(0);

    [Fact]
    public void isinf_returns_1_for_positive_infinity() => isinf(double.PositiveInfinity).ShouldBe(1);

    [Fact]
    public void isinf_returns_1_for_negative_infinity() => isinf(double.NegativeInfinity).ShouldBe(1);

    [Fact]
    public void isinf_returns_0_for_finite() => isinf(1.0).ShouldBe(0);

    [Fact]
    public void isfinite_returns_1_for_finite() => isfinite(1.0).ShouldBe(1);

    [Fact]
    public void isfinite_returns_0_for_nan() => isfinite(double.NaN).ShouldBe(0);

    [Fact]
    public void isfinite_returns_0_for_infinity() => isfinite(double.PositiveInfinity).ShouldBe(0);

    // -----------------------------------------------------------------
    // Constants
    // -----------------------------------------------------------------

    [Fact]
    public void M_PI_matches_Math_PI() => M_PI.ShouldBe(Math.PI);

    [Fact]
    public void M_E_matches_Math_E() => M_E.ShouldBe(Math.E);

    [Fact]
    public void NAN_is_nan() => double.IsNaN(NAN).ShouldBeTrue();

    [Fact]
    public void INFINITY_is_positive_infinity() => double.IsPositiveInfinity(INFINITY).ShouldBeTrue();
}
