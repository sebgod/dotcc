#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace DotCC.Libc;

/// <summary>
/// C99 <c>&lt;math.h&gt;</c> surface. Every unsuffixed function takes and returns
/// <c>double</c> (routes to <see cref="Math"/>), as C says whatever the argument type.
/// The <c>f</c>-suffix variants (<c>sinf</c>, <c>cosf</c>, …) are single precision
/// (route to <see cref="MathF"/>). Only the classification and comparison macros
/// have a <c>float</c> overload.
/// </summary>
/// <remarks>
/// <para>
/// <c>&lt;tgmath.h&gt;</c> is not done here: its C11 <c>_Generic</c> macros pick
/// <c>sinf</c> for a <c>float</c> argument, <c>sin</c> for an integer or
/// <c>double</c>, and <c>csin</c> for a complex one at lowering time. A
/// <c>float</c> overload of <c>sin</c> would only let C# overload resolution
/// compute a plain <c>&lt;math.h&gt;</c> call in single precision, which C does not.
/// </para>
/// <para>
/// All wrappers are <c>AggressiveInlining</c>'d so the BCL call is a
/// direct branch under JIT/AOT — no managed call frame overhead vs. the
/// underlying <see cref="Math"/> / <see cref="MathF"/> method.
/// </para>
/// </remarks>
public static unsafe partial class Libc
{
    // -----------------------------------------------------------------
    // Math constants. C99 <math.h> exposes M_PI / M_E etc. as feature-
    // gated macros (under _USE_MATH_DEFINES on MSVC, always on glibc).
    // We surface them as fields so user code can write `M_PI` directly.
    // -----------------------------------------------------------------

    /// <summary><c>M_PI</c> — ratio of a circle's circumference to its diameter.</summary>
    public const double M_PI = Math.PI;
    /// <summary><c>M_E</c> — Euler's number.</summary>
    public const double M_E = Math.E;
    /// <summary><c>M_SQRT2</c> — √2.</summary>
    public const double M_SQRT2 = 1.41421356237309504880;
    /// <summary><c>M_LN2</c> — natural log of 2.</summary>
    public const double M_LN2 = 0.693147180559945309417;
    /// <summary><c>M_LN10</c> — natural log of 10.</summary>
    public const double M_LN10 = 2.30258509299404568402;

    /// <summary><c>NAN</c> — a quiet IEEE-754 not-a-number with the sign bit clear, as C's is
    /// (.NET's <see cref="double.NaN"/> has it set, which <c>signbit</c> and <c>printf</c> would show).</summary>
    public static double NAN => PositiveNaN;

    /// <summary>The quiet NaN with the sign bit clear, as C's <c>NAN</c> and <c>nan("")</c> give it.</summary>
    private static readonly double PositiveNaN = BitConverter.Int64BitsToDouble(0x7FF8000000000000);

    /// <summary>The single-precision quiet NaN with the sign bit clear.</summary>
    private static readonly float PositiveNaNF = BitConverter.Int32BitsToSingle(0x7FC00000);
    /// <summary><c>INFINITY</c> — positive IEEE-754 infinity.</summary>
    public static double INFINITY => double.PositiveInfinity;
    /// <summary><c>HUGE_VAL</c> — overflow sentinel for double-precision math (== <see cref="INFINITY"/> on IEEE-754 platforms).</summary>
    public static double HUGE_VAL => double.PositiveInfinity;
    /// <summary><c>HUGE_VALF</c> — single-precision counterpart of <see cref="HUGE_VAL"/>.</summary>
    public static float HUGE_VALF => float.PositiveInfinity;

    // -----------------------------------------------------------------
    // Trigonometric — sin / cos / tan + asin / acos / atan / atan2
    // -----------------------------------------------------------------

    /// <summary><c>sin(x)</c> — sine. Argument in radians.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double sin(double x) => Math.Sin(x);
    /// <summary><c>sinf(x)</c> — single-precision <see cref="sin(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float sinf(float x) => MathF.Sin(x);

    /// <summary><c>cos(x)</c> — cosine. Argument in radians.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double cos(double x) => Math.Cos(x);
    /// <summary><c>cosf(x)</c> — single-precision <see cref="cos(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float cosf(float x) => MathF.Cos(x);

    /// <summary><c>tan(x)</c> — tangent. Argument in radians.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double tan(double x) => Math.Tan(x);
    /// <summary><c>tanf(x)</c> — single-precision <see cref="tan(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float tanf(float x) => MathF.Tan(x);

    /// <summary><c>asin(x)</c> — inverse sine. Returns radians in [-π/2, π/2].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double asin(double x) => Math.Asin(x);
    /// <summary><c>asinf(x)</c> — single-precision <see cref="asin(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float asinf(float x) => MathF.Asin(x);

    /// <summary><c>acos(x)</c> — inverse cosine. Returns radians in [0, π].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double acos(double x) => Math.Acos(x);
    /// <summary><c>acosf(x)</c> — single-precision <see cref="acos(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float acosf(float x) => MathF.Acos(x);

    /// <summary><c>atan(x)</c> — inverse tangent. Returns radians in (-π/2, π/2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double atan(double x) => Math.Atan(x);
    /// <summary><c>atanf(x)</c> — single-precision <see cref="atan(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float atanf(float x) => MathF.Atan(x);

    /// <summary><c>atan2(y, x)</c> — angle of vector (x, y), accounting for quadrant. Returns radians in (-π, π].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double atan2(double y, double x) => Math.Atan2(y, x);
    /// <summary><c>atan2f(y, x)</c> — single-precision <see cref="atan2(double, double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float atan2f(float y, float x) => MathF.Atan2(y, x);

    // -----------------------------------------------------------------
    // Hyperbolic (C99) — sinh / cosh / tanh + inverses
    // -----------------------------------------------------------------

    /// <summary><c>sinh(x)</c> — hyperbolic sine.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double sinh(double x) => Math.Sinh(x);
    /// <summary><c>sinhf(x)</c> — single-precision <see cref="sinh(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float sinhf(float x) => MathF.Sinh(x);

    /// <summary><c>cosh(x)</c> — hyperbolic cosine.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double cosh(double x) => Math.Cosh(x);
    /// <summary><c>coshf(x)</c> — single-precision <see cref="cosh(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float coshf(float x) => MathF.Cosh(x);

    /// <summary><c>tanh(x)</c> — hyperbolic tangent.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double tanh(double x) => Math.Tanh(x);
    /// <summary><c>tanhf(x)</c> — single-precision <see cref="tanh(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float tanhf(float x) => MathF.Tanh(x);

    // -----------------------------------------------------------------
    // Exponentials and logarithms
    // -----------------------------------------------------------------

    /// <summary><c>exp(x)</c> — e raised to <paramref name="x"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double exp(double x) => Math.Exp(x);
    /// <summary><c>expf(x)</c> — single-precision <see cref="exp(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float expf(float x) => MathF.Exp(x);

    /// <summary><c>log(x)</c> — natural logarithm.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double log(double x) => Math.Log(x);
    /// <summary><c>logf(x)</c> — single-precision <see cref="log(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float logf(float x) => MathF.Log(x);

    /// <summary><c>log10(x)</c> — base-10 logarithm.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double log10(double x) => Math.Log10(x);
    /// <summary><c>log10f(x)</c> — single-precision <see cref="log10(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float log10f(float x) => MathF.Log10(x);

    /// <summary><c>log2(x)</c> (C99) — base-2 logarithm.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double log2(double x) => Math.Log2(x);
    /// <summary><c>log2f(x)</c> — single-precision <see cref="log2(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float log2f(float x) => MathF.Log2(x);

    // -----------------------------------------------------------------
    // Power and root — pow / sqrt / cbrt
    // -----------------------------------------------------------------

    /// <summary><c>pow(x, y)</c> — <paramref name="x"/> raised to <paramref name="y"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double pow(double x, double y) => Math.Pow(x, y);
    /// <summary><c>powf(x, y)</c> — single-precision <see cref="pow(double, double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float powf(float x, float y) => MathF.Pow(x, y);

    /// <summary>
    /// <c>frexp(x, exp)</c> — split <paramref name="x"/> into a normalized mantissa
    /// in [0.5, 1) (or 0) and an integer exponent such that <c>x = m·2^*exp</c>,
    /// writing the exponent to <paramref name="exp"/>. Zero / NaN / infinity return
    /// <paramref name="x"/> unchanged with <c>*exp = 0</c>. (<c>ilogb</c> gives
    /// floor(log2|x|); frexp's mantissa range is [0.5,1), so the exponent is one
    /// greater and the mantissa is <c>x</c> scaled back down by it.)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double frexp(double x, int* exp)
    {
        if (x == 0.0 || double.IsNaN(x) || double.IsInfinity(x)) { *exp = 0; return x; }
        int e = Math.ILogB(x) + 1;
        *exp = e;
        return Math.ScaleB(x, -e);
    }
    /// <summary><c>frexpf(x, exp)</c> — single-precision <see cref="frexp(double, int*)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float frexpf(float x, int* exp)
    {
        if (x == 0f || float.IsNaN(x) || float.IsInfinity(x)) { *exp = 0; return x; }
        int e = MathF.ILogB(x) + 1;
        *exp = e;
        return MathF.ScaleB(x, -e);
    }

    /// <summary><c>ldexp(x, exp)</c> — <c>x·2^exp</c>, the inverse of <see cref="frexp(double, int*)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ldexp(double x, int exp) => double.IsNaN(x) ? x : Math.ScaleB(x, exp);
    /// <summary><c>ldexpf(x, exp)</c> — explicit single-precision <see cref="ldexp(float, int)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ldexpf(float x, int exp) => float.IsNaN(x) ? x : MathF.ScaleB(x, exp);

    /// <summary><c>sqrt(x)</c> — square root.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double sqrt(double x) => Math.Sqrt(x);
    /// <summary><c>sqrtf(x)</c> — single-precision <see cref="sqrt(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float sqrtf(float x) => MathF.Sqrt(x);

    /// <summary><c>cbrt(x)</c> (C99) — cube root.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double cbrt(double x) => Math.Cbrt(x);
    /// <summary><c>cbrtf(x)</c> — single-precision <see cref="cbrt(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float cbrtf(float x) => MathF.Cbrt(x);

    // -----------------------------------------------------------------
    // Rounding — ceil / floor / round / trunc
    // -----------------------------------------------------------------

    /// <summary><c>ceil(x)</c> — smallest integer ≥ <paramref name="x"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ceil(double x) => Math.Ceiling(x);
    /// <summary><c>ceilf(x)</c> — single-precision <see cref="ceil(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ceilf(float x) => MathF.Ceiling(x);

    /// <summary><c>floor(x)</c> — largest integer ≤ <paramref name="x"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double floor(double x) => Math.Floor(x);
    /// <summary><c>floorf(x)</c> — single-precision <see cref="floor(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float floorf(float x) => MathF.Floor(x);

    /// <summary><c>round(x)</c> (C99) — round to nearest integer, ties away from zero.</summary>
    /// <remarks>BCL's <see cref="Math.Round(double)"/> defaults to banker's rounding (ties to even); we pass <see cref="MidpointRounding.AwayFromZero"/> to match C99 semantics exactly.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double round(double x) => Math.Round(x, MidpointRounding.AwayFromZero);
    /// <summary><c>roundf(x)</c> — single-precision <see cref="round(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float roundf(float x) => MathF.Round(x, MidpointRounding.AwayFromZero);

    /// <summary><c>trunc(x)</c> (C99) — round toward zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double trunc(double x) => Math.Truncate(x);
    /// <summary><c>truncf(x)</c> — single-precision <see cref="trunc(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float truncf(float x) => MathF.Truncate(x);

    // -----------------------------------------------------------------
    // Absolute value, remainder, min/max
    // -----------------------------------------------------------------

    /// <summary><c>fabs(x)</c> — floating-point absolute value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double fabs(double x) => Math.Abs(x);
    /// <summary><c>fabsf(x)</c> — single-precision <see cref="fabs(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float fabsf(float x) => MathF.Abs(x);
    /// <summary><c>fabsl(x)</c> — <c>long double</c> <see cref="fabs(double)"/>
    /// (dotcc models long double as 64-bit, so this IS the double form).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double fabsl(double x) => Math.Abs(x);

    /// <summary><c>modf(x, iptr)</c>: <paramref name="x"/>'s integral part into <paramref name="iptr"/> and its
    /// fractional part returned, both with <paramref name="x"/>'s sign (an infinity's fraction is a signed zero).</summary>
    public static unsafe double modf(double x, double* iptr)
    {
        var integral = Math.Truncate(x);
        *iptr = integral;
        return double.IsInfinity(x) ? Math.CopySign(0.0, x) : Math.CopySign(x - integral, x);
    }

    /// <summary><c>modff(x, iptr)</c>: single-precision <see cref="modf(double, double*)"/>.</summary>
    public static unsafe float modff(float x, float* iptr)
    {
        double integral;
        var fraction = modf(x, &integral);
        *iptr = (float)integral;
        return (float)fraction;
    }

    /// <summary><c>hypot(x, y)</c>: sqrt(x² + y²) without undue overflow or underflow.</summary>
    public static double hypot(double x, double y) => double.Hypot(x, y);

    /// <summary><c>hypotf(x, y)</c>: single-precision <see cref="hypot(double, double)"/>.</summary>
    public static float hypotf(float x, float y) => float.Hypot(x, y);

    /// <summary><c>copysign(x, y)</c>: <paramref name="x"/>'s magnitude with <paramref name="y"/>'s sign.</summary>
    public static double copysign(double x, double y) => Math.CopySign(x, y);

    /// <summary><c>copysignf(x, y)</c>: single-precision <see cref="copysign(double, double)"/>.</summary>
    public static float copysignf(float x, float y) => MathF.CopySign(x, y);

    /// <summary><c>fmod(x, y)</c> — floating-point remainder of <paramref name="x"/> / <paramref name="y"/>. Sign matches <paramref name="x"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double fmod(double x, double y)
    {
        if (double.IsNaN(x) || double.IsNaN(y)) return double.NaN;
        var r = Math.IEEERemainder(x, y);
        if (r != 0 && SafeSign(r) != SafeSign(x)) r += Math.CopySign(y, x);
        return r;
    }
    static int SafeSign(double v) => double.IsNaN(v) ? 0 : Math.Sign(v);
    /// <summary><c>fmodf(x, y)</c> — single-precision <see cref="fmod(double, double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float fmodf(float x, float y) => (float)fmod((double)x, (double)y);

    /// <summary><c>fmin(x, y)</c> (C99) — minimum, IEEE-754-aware (treats NaN as "missing").</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double fmin(double x, double y) => Math.Min(x, y);
    /// <summary><c>fminf(x, y)</c> — single-precision <see cref="fmin(double, double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float fminf(float x, float y) => MathF.Min(x, y);

    /// <summary><c>fmax(x, y)</c> (C99) — maximum, IEEE-754-aware.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double fmax(double x, double y) => Math.Max(x, y);
    /// <summary><c>fmaxf(x, y)</c> — single-precision <see cref="fmax(double, double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float fmaxf(float x, float y) => MathF.Max(x, y);

    // -----------------------------------------------------------------
    // IEEE-754 classification — isnan / isinf / isfinite
    //
    // C99 declares these as macros (so they're type-generic); we expose
    // them as overloaded methods, which gives equivalent dispatch through
    // C# overload resolution. Return type is `int` to match real C
    // semantics — C99 isnan/isinf return non-zero on match, zero
    // otherwise (NOT a `bool`).
    // -----------------------------------------------------------------

    /// <summary><c>isnan(x)</c> — 1 if <paramref name="x"/> is NaN, 0 otherwise.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int isnan(double x) => double.IsNaN(x) ? 1 : 0;
    /// <inheritdoc cref="isnan(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int isnan(float x) => float.IsNaN(x) ? 1 : 0;

    /// <summary><c>isinf(x)</c> — 1 if <paramref name="x"/> is ±∞, 0 otherwise.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int isinf(double x) => double.IsInfinity(x) ? 1 : 0;
    /// <inheritdoc cref="isinf(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int isinf(float x) => float.IsInfinity(x) ? 1 : 0;

    /// <summary><c>isfinite(x)</c> (C99) — 1 if <paramref name="x"/> is finite (not NaN and not infinite), 0 otherwise.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int isfinite(double x) => double.IsFinite(x) ? 1 : 0;
    /// <inheritdoc cref="isfinite(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int isfinite(float x) => float.IsFinite(x) ? 1 : 0;
}
