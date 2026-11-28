#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace DotCC.Libc;

/// <summary>
/// The rest of C99's <c>&lt;math.h&gt;</c> (7.12) beyond MathLib.cs: the inverse hyperbolics,
/// <c>exp2</c> / <c>expm1</c> / <c>log1p</c> / <c>logb</c> / <c>ilogb</c> / <c>scalbn</c>, the
/// remainder and difference functions, <c>fma</c>, the nearest-integer family, <c>nextafter</c>,
/// <c>nan</c>, the classification macros, and every function's <c>long double</c> <c>…l</c> form
/// (<c>long double</c> is <c>double</c> in dotcc). Each computes in <c>double</c> under its C
/// name and has an explicit <c>…f</c> single-precision form, as MathLib's do. Only the
/// classification and comparison macros are overloaded for <c>float</c>.
/// </summary>
/// <remarks>
/// <c>expm1</c> and <c>log1p</c> exist for their accuracy near zero, which the naive
/// <c>exp(x) - 1</c> and <c>log(1 + x)</c> lose; .NET's <c>double.ExpM1</c> / <c>LogP1</c> are those
/// naive forms, so Kahan's and Goldberg's corrections are used instead (a few ulps). The
/// rounding functions use C's default mode, to nearest with ties to even. <c>erf</c>,
/// <c>erfc</c>, <c>tgamma</c> and <c>lgamma</c> have no BCL primitive and are not provided.
/// </remarks>
public static unsafe partial class Libc
{
    // ---- inverse hyperbolic ------------------------------------------------

    /// <summary><c>acosh(x)</c> (C99): inverse hyperbolic cosine.</summary>
    public static double acosh(double x) => Math.Acosh(x);
    /// <summary><c>acoshf(x)</c>.</summary>
    public static float acoshf(float x) => MathF.Acosh(x);
    /// <summary><c>asinh(x)</c> (C99): inverse hyperbolic sine.</summary>
    public static double asinh(double x) => Math.Asinh(x);
    /// <summary><c>asinhf(x)</c>.</summary>
    public static float asinhf(float x) => MathF.Asinh(x);
    /// <summary><c>atanh(x)</c> (C99): inverse hyperbolic tangent.</summary>
    public static double atanh(double x) => Math.Atanh(x);
    /// <summary><c>atanhf(x)</c>.</summary>
    public static float atanhf(float x) => MathF.Atanh(x);

    // ---- exponential and logarithmic -----------------------------------------

    /// <summary><c>exp2(x)</c> (C99): 2 raised to <paramref name="x"/>, exact for an integer.</summary>
    public static double exp2(double x) => Math.Pow(2.0, x);
    /// <summary><c>exp2f(x)</c>.</summary>
    public static float exp2f(float x) => MathF.Pow(2f, x);

    /// <summary><c>expm1(x)</c> (C99): <c>e^x - 1</c>, accurate where <paramref name="x"/> is
    /// near zero. Kahan's correction: <c>(u - 1) * x / log(u)</c> with <c>u = e^x</c> cancels the
    /// rounding error of <c>u</c>.</summary>
    public static double expm1(double x)
    {
        var u = Math.Exp(x);
        if (u == 1.0) { return x; }                       // |x| below half an ulp of 1 (and +-0)
        if (double.IsPositiveInfinity(u) || double.IsNaN(u)) { return u; }
        var um1 = u - 1.0;
        if (um1 == -1.0) { return -1.0; }
        return um1 * x / Math.Log(u);
    }
    /// <summary><c>expm1f(x)</c>.</summary>
    public static float expm1f(float x) => (float)expm1((double)x);

    /// <summary><c>log1p(x)</c> (C99): <c>log(1 + x)</c>, accurate where <paramref name="x"/> is
    /// near zero. Goldberg's correction: <c>log(u) * x / (u - 1)</c> with <c>u = 1 + x</c>.</summary>
    public static double log1p(double x)
    {
        if (double.IsPositiveInfinity(x) || double.IsNaN(x)) { return x; }
        var u = 1.0 + x;
        if (u == 1.0) { return x; }                       // and +-0 keeps its sign
        return Math.Log(u) * x / (u - 1.0);
    }
    /// <summary><c>log1pf(x)</c>.</summary>
    public static float log1pf(float x) => (float)log1p((double)x);

    /// <summary><c>logb(x)</c> (C99): the unbiased exponent of <paramref name="x"/> as a
    /// floating value; <c>-inf</c> for zero, <c>+inf</c> for an infinity, NaN for NaN.</summary>
    public static double logb(double x) =>
        double.IsNaN(x) ? x : double.IsInfinity(x) ? double.PositiveInfinity : x == 0 ? double.NegativeInfinity : Math.ILogB(x);
    /// <summary><c>logbf(x)</c>.</summary>
    public static float logbf(float x) => (float)logb((double)x);

    /// <summary><c>ilogb(x)</c> (C99): the unbiased exponent as an <c>int</c>; <c>FP_ILOGB0</c> for
    /// zero and <c>FP_ILOGBNAN</c> for NaN (both <c>INT_MIN</c>, as glibc has them), <c>INT_MAX</c>
    /// for an infinity.</summary>
    public static int ilogb(double x) =>
        double.IsNaN(x) || x == 0 ? int.MinValue : double.IsInfinity(x) ? int.MaxValue : Math.ILogB(x);
    /// <summary><c>ilogbf(x)</c>.</summary>
    public static int ilogbf(float x) => ilogb((double)x);

    /// <summary><c>scalbn(x, n)</c> (C99): <c>x * 2^n</c>, computed exactly by exponent.</summary>
    public static double scalbn(double x, int n) => Math.ScaleB(x, n);
    /// <summary><c>scalbnf(x, n)</c>.</summary>
    public static float scalbnf(float x, int n) => MathF.ScaleB(x, n);
    /// <summary><c>scalbln(x, n)</c> (C99): <see cref="scalbn(double, int)"/> with a <c>long</c>
    /// exponent, clamped to the range where the result is still decided.</summary>
    public static double scalbln(double x, long n) => Math.ScaleB(x, (int)Math.Clamp(n, -100000L, 100000L));
    /// <summary><c>scalblnf(x, n)</c>.</summary>
    public static float scalblnf(float x, long n) => MathF.ScaleB(x, (int)Math.Clamp(n, -100000L, 100000L));

    // ---- remainder, difference, fused multiply-add ---------------------------

    /// <summary><c>remainder(x, y)</c> (C99): the IEEE remainder <c>x - n*y</c>, <c>n</c> the
    /// integer nearest <c>x/y</c> (ties to even).</summary>
    public static double remainder(double x, double y) => Math.IEEERemainder(x, y);
    /// <summary><c>remainderf(x, y)</c>.</summary>
    public static float remainderf(float x, float y) => MathF.IEEERemainder(x, y);

    /// <summary><c>remquo(x, y, quo)</c> (C99): <see cref="remainder(double, double)"/>, with the
    /// sign of <c>x/y</c> and its integral quotient's low 31 bits stored through
    /// <paramref name="quo"/>.</summary>
    public static double remquo(double x, double y, int* quo)
    {
        var r = Math.IEEERemainder(x, y);
        var q = Math.Round((x - r) / y);
        if (quo != null)
        {
            var bits = double.IsFinite(q) ? (int)(Math.Abs(q) % 2147483648.0) : 0;
            *quo = q < 0 ? -bits : bits;
        }
        return r;
    }
    /// <summary><c>remquof(x, y, quo)</c>.</summary>
    public static float remquof(float x, float y, int* quo) => (float)remquo((double)x, (double)y, quo);

    /// <summary><c>fdim(x, y)</c> (C99): <c>x - y</c> when <c>x &gt; y</c>, else <c>+0</c>; NaN
    /// when either is.</summary>
    public static double fdim(double x, double y) => double.IsNaN(x) || double.IsNaN(y) ? x + y : x > y ? x - y : 0.0;
    /// <summary><c>fdimf(x, y)</c>.</summary>
    public static float fdimf(float x, float y) => float.IsNaN(x) || float.IsNaN(y) ? x + y : x > y ? x - y : 0f;

    /// <summary><c>fma(x, y, z)</c> (C99): <c>x * y + z</c> rounded once.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double fma(double x, double y, double z) => Math.FusedMultiplyAdd(x, y, z);
    /// <summary><c>fmaf(x, y, z)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float fmaf(float x, float y, float z) => MathF.FusedMultiplyAdd(x, y, z);

    // ---- nearest integer -------------------------------------------------------

    /// <summary><c>rint(x)</c> (C99): <paramref name="x"/> rounded in the current mode (to
    /// nearest, ties to even).</summary>
    public static double rint(double x) => Math.Round(x, MidpointRounding.ToEven);
    /// <summary><c>rintf(x)</c>.</summary>
    public static float rintf(float x) => MathF.Round(x, MidpointRounding.ToEven);
    /// <summary><c>nearbyint(x)</c> (C99): <see cref="rint(double)"/> (no floating-point exception
    /// state is kept, so the two are one).</summary>
    public static double nearbyint(double x) => Math.Round(x, MidpointRounding.ToEven);
    /// <summary><c>nearbyintf(x)</c>.</summary>
    public static float nearbyintf(float x) => MathF.Round(x, MidpointRounding.ToEven);
    /// <summary><c>lrint(x)</c> (C99): <see cref="rint(double)"/> as a <c>long</c>.</summary>
    public static long lrint(double x) => (long)Math.Round(x, MidpointRounding.ToEven);
    /// <summary><c>lrintf(x)</c>.</summary>
    public static long lrintf(float x) => (long)MathF.Round(x, MidpointRounding.ToEven);
    /// <summary><c>llrint(x)</c> (C99): <see cref="lrint(double)"/>, <c>long long</c> being 64-bit too.</summary>
    public static long llrint(double x) => (long)Math.Round(x, MidpointRounding.ToEven);
    /// <summary><c>llrintf(x)</c>.</summary>
    public static long llrintf(float x) => (long)MathF.Round(x, MidpointRounding.ToEven);
    /// <summary><c>lround(x)</c> (C99): <paramref name="x"/> rounded half away from zero, as a <c>long</c>.</summary>
    public static long lround(double x) => (long)Math.Round(x, MidpointRounding.AwayFromZero);
    /// <summary><c>lroundf(x)</c>.</summary>
    public static long lroundf(float x) => (long)MathF.Round(x, MidpointRounding.AwayFromZero);
    /// <summary><c>llround(x)</c> (C99): <see cref="lround(double)"/>.</summary>
    public static long llround(double x) => (long)Math.Round(x, MidpointRounding.AwayFromZero);
    /// <summary><c>llroundf(x)</c>.</summary>
    public static long llroundf(float x) => (long)MathF.Round(x, MidpointRounding.AwayFromZero);

    // ---- neighbours and NaN -----------------------------------------------------

    /// <summary><c>nextafter(x, y)</c> (C99): the representable value after <paramref name="x"/>
    /// toward <paramref name="y"/>; <paramref name="y"/> when they are equal, NaN when either is.</summary>
    public static double nextafter(double x, double y) =>
        double.IsNaN(x) || double.IsNaN(y) ? x + y : x == y ? y : x < y ? Math.BitIncrement(x) : Math.BitDecrement(x);
    /// <summary><c>nextafterf(x, y)</c>.</summary>
    public static float nextafterf(float x, float y) =>
        float.IsNaN(x) || float.IsNaN(y) ? x + y : x == y ? y : x < y ? MathF.BitIncrement(x) : MathF.BitDecrement(x);
    /// <summary><c>nexttoward(x, y)</c> (C99): <see cref="nextafter(double, double)"/> with a
    /// <c>long double</c> target, which is <c>double</c> here.</summary>
    public static double nexttoward(double x, double y) => nextafter(x, y);
    /// <summary><c>nexttowardf(x, y)</c>: toward a <c>long double</c> target.</summary>
    public static float nexttowardf(float x, double y) =>
        double.IsNaN(x) || double.IsNaN(y) ? (float)(x + y) : x == y ? (float)y : x < y ? MathF.BitIncrement(x) : MathF.BitDecrement(x);

    /// <summary><c>nan(tagp)</c> (C99): a quiet NaN (the payload text is not encoded).</summary>
    public static double nan(byte* tagp) => PositiveNaN;
    /// <summary><c>nanf(tagp)</c>.</summary>
    public static float nanf(byte* tagp) => PositiveNaNF;
    /// <summary><c>nanl(tagp)</c>.</summary>
    public static double nanl(byte* tagp) => PositiveNaN;

    // ---- classification and comparison (C99 macros, here overloads) ------------

    /// <summary><c>signbit(x)</c> (C99): non-zero when the sign bit is set (<c>-0.0</c> and a
    /// negative NaN included).</summary>
    public static int signbit(double x) => double.IsNegative(x) ? 1 : 0;
    /// <inheritdoc cref="signbit(double)"/>
    public static int signbit(float x) => float.IsNegative(x) ? 1 : 0;
    /// <summary><c>isnormal(x)</c> (C99): 1 for a normal value (not zero, subnormal, infinite or NaN).</summary>
    public static int isnormal(double x) => double.IsNormal(x) ? 1 : 0;
    /// <inheritdoc cref="isnormal(double)"/>
    public static int isnormal(float x) => float.IsNormal(x) ? 1 : 0;
    /// <summary><c>fpclassify(x)</c> (C99): <c>FP_NAN</c> 0, <c>FP_INFINITE</c> 1, <c>FP_ZERO</c> 2,
    /// <c>FP_SUBNORMAL</c> 3 or <c>FP_NORMAL</c> 4 (glibc's values).</summary>
    public static int fpclassify(double x) =>
        double.IsNaN(x) ? 0 : double.IsInfinity(x) ? 1 : x == 0 ? 2 : double.IsSubnormal(x) ? 3 : 4;
    /// <inheritdoc cref="fpclassify(double)"/>
    public static int fpclassify(float x) =>
        float.IsNaN(x) ? 0 : float.IsInfinity(x) ? 1 : x == 0 ? 2 : float.IsSubnormal(x) ? 3 : 4;
    /// <summary><c>isgreater(x, y)</c> (C99): <c>x &gt; y</c> without raising on NaN.</summary>
    public static int isgreater(double x, double y) => x > y ? 1 : 0;
    /// <summary><c>isgreaterequal(x, y)</c> (C99).</summary>
    public static int isgreaterequal(double x, double y) => x >= y ? 1 : 0;
    /// <summary><c>isless(x, y)</c> (C99).</summary>
    public static int isless(double x, double y) => x < y ? 1 : 0;
    /// <summary><c>islessequal(x, y)</c> (C99).</summary>
    public static int islessequal(double x, double y) => x <= y ? 1 : 0;
    /// <summary><c>islessgreater(x, y)</c> (C99): <c>x &lt; y || x &gt; y</c>.</summary>
    public static int islessgreater(double x, double y) => x < y || x > y ? 1 : 0;
    /// <summary><c>isunordered(x, y)</c> (C99): 1 when either is NaN.</summary>
    public static int isunordered(double x, double y) => double.IsNaN(x) || double.IsNaN(y) ? 1 : 0;

    // ---- the long double forms (long double is double) ---------------------------

    /// <summary><c>sinl(x)</c>.</summary>
    public static double sinl(double x) => Math.Sin(x);
    /// <summary><c>cosl(x)</c>.</summary>
    public static double cosl(double x) => Math.Cos(x);
    /// <summary><c>tanl(x)</c>.</summary>
    public static double tanl(double x) => Math.Tan(x);
    /// <summary><c>asinl(x)</c>.</summary>
    public static double asinl(double x) => Math.Asin(x);
    /// <summary><c>acosl(x)</c>.</summary>
    public static double acosl(double x) => Math.Acos(x);
    /// <summary><c>atanl(x)</c>.</summary>
    public static double atanl(double x) => Math.Atan(x);
    /// <summary><c>atan2l(y, x)</c>.</summary>
    public static double atan2l(double y, double x) => Math.Atan2(y, x);
    /// <summary><c>sinhl(x)</c>.</summary>
    public static double sinhl(double x) => Math.Sinh(x);
    /// <summary><c>coshl(x)</c>.</summary>
    public static double coshl(double x) => Math.Cosh(x);
    /// <summary><c>tanhl(x)</c>.</summary>
    public static double tanhl(double x) => Math.Tanh(x);
    /// <summary><c>acoshl(x)</c>.</summary>
    public static double acoshl(double x) => Math.Acosh(x);
    /// <summary><c>asinhl(x)</c>.</summary>
    public static double asinhl(double x) => Math.Asinh(x);
    /// <summary><c>atanhl(x)</c>.</summary>
    public static double atanhl(double x) => Math.Atanh(x);
    /// <summary><c>expl(x)</c>.</summary>
    public static double expl(double x) => Math.Exp(x);
    /// <summary><c>exp2l(x)</c>.</summary>
    public static double exp2l(double x) => exp2(x);
    /// <summary><c>expm1l(x)</c>.</summary>
    public static double expm1l(double x) => expm1(x);
    /// <summary><c>logl(x)</c>.</summary>
    public static double logl(double x) => Math.Log(x);
    /// <summary><c>log10l(x)</c>.</summary>
    public static double log10l(double x) => Math.Log10(x);
    /// <summary><c>log2l(x)</c>.</summary>
    public static double log2l(double x) => Math.Log2(x);
    /// <summary><c>log1pl(x)</c>.</summary>
    public static double log1pl(double x) => log1p(x);
    /// <summary><c>logbl(x)</c>.</summary>
    public static double logbl(double x) => logb(x);
    /// <summary><c>ilogbl(x)</c>.</summary>
    public static int ilogbl(double x) => ilogb(x);
    /// <summary><c>scalbnl(x, n)</c>.</summary>
    public static double scalbnl(double x, int n) => Math.ScaleB(x, n);
    /// <summary><c>scalblnl(x, n)</c>.</summary>
    public static double scalblnl(double x, long n) => scalbln(x, n);
    /// <summary><c>powl(x, y)</c>.</summary>
    public static double powl(double x, double y) => Math.Pow(x, y);
    /// <summary><c>sqrtl(x)</c>.</summary>
    public static double sqrtl(double x) => Math.Sqrt(x);
    /// <summary><c>cbrtl(x)</c>.</summary>
    public static double cbrtl(double x) => Math.Cbrt(x);
    /// <summary><c>hypotl(x, y)</c>.</summary>
    public static double hypotl(double x, double y) => double.Hypot(x, y);
    /// <summary><c>frexpl(x, e)</c>.</summary>
    public static double frexpl(double x, int* e) => frexp(x, e);
    /// <summary><c>ldexpl(x, e)</c>.</summary>
    public static double ldexpl(double x, int e) => ldexp(x, e);
    /// <summary><c>modfl(x, iptr)</c>.</summary>
    public static double modfl(double x, double* iptr) => modf(x, iptr);
    /// <summary><c>ceill(x)</c>.</summary>
    public static double ceill(double x) => Math.Ceiling(x);
    /// <summary><c>floorl(x)</c>.</summary>
    public static double floorl(double x) => Math.Floor(x);
    /// <summary><c>roundl(x)</c>.</summary>
    public static double roundl(double x) => Math.Round(x, MidpointRounding.AwayFromZero);
    /// <summary><c>truncl(x)</c>.</summary>
    public static double truncl(double x) => Math.Truncate(x);
    /// <summary><c>rintl(x)</c>.</summary>
    public static double rintl(double x) => rint(x);
    /// <summary><c>nearbyintl(x)</c>.</summary>
    public static double nearbyintl(double x) => rint(x);
    /// <summary><c>lrintl(x)</c>.</summary>
    public static long lrintl(double x) => lrint(x);
    /// <summary><c>llrintl(x)</c>.</summary>
    public static long llrintl(double x) => lrint(x);
    /// <summary><c>lroundl(x)</c>.</summary>
    public static long lroundl(double x) => lround(x);
    /// <summary><c>llroundl(x)</c>.</summary>
    public static long llroundl(double x) => lround(x);
    /// <summary><c>fmodl(x, y)</c>.</summary>
    public static double fmodl(double x, double y) => fmod(x, y);
    /// <summary><c>remainderl(x, y)</c>.</summary>
    public static double remainderl(double x, double y) => Math.IEEERemainder(x, y);
    /// <summary><c>remquol(x, y, quo)</c>.</summary>
    public static double remquol(double x, double y, int* quo) => remquo(x, y, quo);
    /// <summary><c>copysignl(x, y)</c>.</summary>
    public static double copysignl(double x, double y) => Math.CopySign(x, y);
    /// <summary><c>nextafterl(x, y)</c>.</summary>
    public static double nextafterl(double x, double y) => nextafter(x, y);
    /// <summary><c>nexttowardl(x, y)</c>.</summary>
    public static double nexttowardl(double x, double y) => nextafter(x, y);
    /// <summary><c>fdiml(x, y)</c>.</summary>
    public static double fdiml(double x, double y) => fdim(x, y);
    /// <summary><c>fmaxl(x, y)</c>.</summary>
    public static double fmaxl(double x, double y) => fmax(x, y);
    /// <summary><c>fminl(x, y)</c>.</summary>
    public static double fminl(double x, double y) => fmin(x, y);
    /// <summary><c>fmal(x, y, z)</c>.</summary>
    public static double fmal(double x, double y, double z) => Math.FusedMultiplyAdd(x, y, z);
}
