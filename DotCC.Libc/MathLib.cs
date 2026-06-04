#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace DotCC.Libc;

/// <summary>
/// C99 <c>&lt;math.h&gt;</c> surface. Every function exists as a
/// <c>double</c> overload (routes to <see cref="Math"/>) AND a
/// <c>float</c> overload (routes to <see cref="MathF"/>). The explicit
/// <c>f</c>-suffix variants (<c>sinf</c>, <c>cosf</c>, …) are also
/// exposed for C source that prefers the C99-explicit naming.
/// </summary>
/// <remarks>
/// <para>
/// <c>&lt;tgmath.h&gt;</c> in real C uses C11 <c>_Generic</c> to dispatch
/// <c>sin(x)</c> to <c>sinf</c> when <c>x</c> is <c>float</c>. dotcc
/// sidesteps the <c>_Generic</c> requirement entirely: because C# already
/// has function overloading, defining <c>sin(double)</c> and
/// <c>sin(float)</c> here gives us the same type-driven dispatch at the
/// C# compiler's overload-resolution step. So <c>tgmath.h</c> is the
/// same synthetic header content as <c>math.h</c> — no separate
/// implementation needed.
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

    /// <summary><c>NAN</c> — a quiet IEEE-754 not-a-number.</summary>
    public static double NAN => double.NaN;
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
    /// <inheritdoc cref="sin(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float sin(float x) => MathF.Sin(x);
    /// <summary><c>sinf(x)</c> — explicit single-precision <see cref="sin(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float sinf(float x) => MathF.Sin(x);

    /// <summary><c>cos(x)</c> — cosine. Argument in radians.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double cos(double x) => Math.Cos(x);
    /// <inheritdoc cref="cos(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float cos(float x) => MathF.Cos(x);
    /// <summary><c>cosf(x)</c> — explicit single-precision <see cref="cos(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float cosf(float x) => MathF.Cos(x);

    /// <summary><c>tan(x)</c> — tangent. Argument in radians.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double tan(double x) => Math.Tan(x);
    /// <inheritdoc cref="tan(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float tan(float x) => MathF.Tan(x);
    /// <summary><c>tanf(x)</c> — explicit single-precision <see cref="tan(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float tanf(float x) => MathF.Tan(x);

    /// <summary><c>asin(x)</c> — inverse sine. Returns radians in [-π/2, π/2].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double asin(double x) => Math.Asin(x);
    /// <inheritdoc cref="asin(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float asin(float x) => MathF.Asin(x);
    /// <summary><c>asinf(x)</c> — explicit single-precision <see cref="asin(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float asinf(float x) => MathF.Asin(x);

    /// <summary><c>acos(x)</c> — inverse cosine. Returns radians in [0, π].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double acos(double x) => Math.Acos(x);
    /// <inheritdoc cref="acos(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float acos(float x) => MathF.Acos(x);
    /// <summary><c>acosf(x)</c> — explicit single-precision <see cref="acos(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float acosf(float x) => MathF.Acos(x);

    /// <summary><c>atan(x)</c> — inverse tangent. Returns radians in (-π/2, π/2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double atan(double x) => Math.Atan(x);
    /// <inheritdoc cref="atan(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float atan(float x) => MathF.Atan(x);
    /// <summary><c>atanf(x)</c> — explicit single-precision <see cref="atan(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float atanf(float x) => MathF.Atan(x);

    /// <summary><c>atan2(y, x)</c> — angle of vector (x, y), accounting for quadrant. Returns radians in (-π, π].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double atan2(double y, double x) => Math.Atan2(y, x);
    /// <inheritdoc cref="atan2(double, double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float atan2(float y, float x) => MathF.Atan2(y, x);
    /// <summary><c>atan2f(y, x)</c> — explicit single-precision <see cref="atan2(float, float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float atan2f(float y, float x) => MathF.Atan2(y, x);

    // -----------------------------------------------------------------
    // Hyperbolic (C99) — sinh / cosh / tanh + inverses
    // -----------------------------------------------------------------

    /// <summary><c>sinh(x)</c> — hyperbolic sine.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double sinh(double x) => Math.Sinh(x);
    /// <inheritdoc cref="sinh(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float sinh(float x) => MathF.Sinh(x);
    /// <summary><c>sinhf(x)</c> — explicit single-precision <see cref="sinh(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float sinhf(float x) => MathF.Sinh(x);

    /// <summary><c>cosh(x)</c> — hyperbolic cosine.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double cosh(double x) => Math.Cosh(x);
    /// <inheritdoc cref="cosh(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float cosh(float x) => MathF.Cosh(x);
    /// <summary><c>coshf(x)</c> — explicit single-precision <see cref="cosh(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float coshf(float x) => MathF.Cosh(x);

    /// <summary><c>tanh(x)</c> — hyperbolic tangent.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double tanh(double x) => Math.Tanh(x);
    /// <inheritdoc cref="tanh(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float tanh(float x) => MathF.Tanh(x);
    /// <summary><c>tanhf(x)</c> — explicit single-precision <see cref="tanh(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float tanhf(float x) => MathF.Tanh(x);

    // -----------------------------------------------------------------
    // Exponentials and logarithms
    // -----------------------------------------------------------------

    /// <summary><c>exp(x)</c> — e raised to <paramref name="x"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double exp(double x) => Math.Exp(x);
    /// <inheritdoc cref="exp(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float exp(float x) => MathF.Exp(x);
    /// <summary><c>expf(x)</c> — explicit single-precision <see cref="exp(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float expf(float x) => MathF.Exp(x);

    /// <summary><c>log(x)</c> — natural logarithm.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double log(double x) => Math.Log(x);
    /// <inheritdoc cref="log(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float log(float x) => MathF.Log(x);
    /// <summary><c>logf(x)</c> — explicit single-precision <see cref="log(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float logf(float x) => MathF.Log(x);

    /// <summary><c>log10(x)</c> — base-10 logarithm.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double log10(double x) => Math.Log10(x);
    /// <inheritdoc cref="log10(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float log10(float x) => MathF.Log10(x);
    /// <summary><c>log10f(x)</c> — explicit single-precision <see cref="log10(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float log10f(float x) => MathF.Log10(x);

    /// <summary><c>log2(x)</c> (C99) — base-2 logarithm.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double log2(double x) => Math.Log2(x);
    /// <inheritdoc cref="log2(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float log2(float x) => MathF.Log2(x);
    /// <summary><c>log2f(x)</c> — explicit single-precision <see cref="log2(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float log2f(float x) => MathF.Log2(x);

    // -----------------------------------------------------------------
    // Power and root — pow / sqrt / cbrt
    // -----------------------------------------------------------------

    /// <summary><c>pow(x, y)</c> — <paramref name="x"/> raised to <paramref name="y"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double pow(double x, double y) => Math.Pow(x, y);
    /// <inheritdoc cref="pow(double, double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float pow(float x, float y) => MathF.Pow(x, y);
    /// <summary><c>powf(x, y)</c> — explicit single-precision <see cref="pow(float, float)"/>.</summary>
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
    /// <inheritdoc cref="frexp(double, int*)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float frexp(float x, int* exp)
    {
        if (x == 0f || float.IsNaN(x) || float.IsInfinity(x)) { *exp = 0; return x; }
        int e = MathF.ILogB(x) + 1;
        *exp = e;
        return MathF.ScaleB(x, -e);
    }
    /// <summary><c>frexpf(x, exp)</c> — explicit single-precision <see cref="frexp(float, int*)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float frexpf(float x, int* exp) => frexp(x, exp);

    /// <summary><c>ldexp(x, exp)</c> — <c>x·2^exp</c>, the inverse of <see cref="frexp(double, int*)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ldexp(double x, int exp) => double.IsNaN(x) ? x : Math.ScaleB(x, exp);
    /// <inheritdoc cref="ldexp(double, int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ldexp(float x, int exp) => float.IsNaN(x) ? x : MathF.ScaleB(x, exp);
    /// <summary><c>ldexpf(x, exp)</c> — explicit single-precision <see cref="ldexp(float, int)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ldexpf(float x, int exp) => float.IsNaN(x) ? x : MathF.ScaleB(x, exp);

    /// <summary><c>sqrt(x)</c> — square root.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double sqrt(double x) => Math.Sqrt(x);
    /// <inheritdoc cref="sqrt(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float sqrt(float x) => MathF.Sqrt(x);
    /// <summary><c>sqrtf(x)</c> — explicit single-precision <see cref="sqrt(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float sqrtf(float x) => MathF.Sqrt(x);

    /// <summary><c>cbrt(x)</c> (C99) — cube root.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double cbrt(double x) => Math.Cbrt(x);
    /// <inheritdoc cref="cbrt(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float cbrt(float x) => MathF.Cbrt(x);
    /// <summary><c>cbrtf(x)</c> — explicit single-precision <see cref="cbrt(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float cbrtf(float x) => MathF.Cbrt(x);

    // -----------------------------------------------------------------
    // Rounding — ceil / floor / round / trunc
    // -----------------------------------------------------------------

    /// <summary><c>ceil(x)</c> — smallest integer ≥ <paramref name="x"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ceil(double x) => Math.Ceiling(x);
    /// <inheritdoc cref="ceil(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ceil(float x) => MathF.Ceiling(x);
    /// <summary><c>ceilf(x)</c> — explicit single-precision <see cref="ceil(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ceilf(float x) => MathF.Ceiling(x);

    /// <summary><c>floor(x)</c> — largest integer ≤ <paramref name="x"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double floor(double x) => Math.Floor(x);
    /// <inheritdoc cref="floor(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float floor(float x) => MathF.Floor(x);
    /// <summary><c>floorf(x)</c> — explicit single-precision <see cref="floor(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float floorf(float x) => MathF.Floor(x);

    /// <summary><c>round(x)</c> (C99) — round to nearest integer, ties away from zero.</summary>
    /// <remarks>BCL's <see cref="Math.Round(double)"/> defaults to banker's rounding (ties to even); we pass <see cref="MidpointRounding.AwayFromZero"/> to match C99 semantics exactly.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double round(double x) => Math.Round(x, MidpointRounding.AwayFromZero);
    /// <inheritdoc cref="round(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float round(float x) => MathF.Round(x, MidpointRounding.AwayFromZero);
    /// <summary><c>roundf(x)</c> — explicit single-precision <see cref="round(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float roundf(float x) => MathF.Round(x, MidpointRounding.AwayFromZero);

    /// <summary><c>trunc(x)</c> (C99) — round toward zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double trunc(double x) => Math.Truncate(x);
    /// <inheritdoc cref="trunc(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float trunc(float x) => MathF.Truncate(x);
    /// <summary><c>truncf(x)</c> — explicit single-precision <see cref="trunc(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float truncf(float x) => MathF.Truncate(x);

    // -----------------------------------------------------------------
    // Absolute value, remainder, min/max
    // -----------------------------------------------------------------

    /// <summary><c>fabs(x)</c> — floating-point absolute value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double fabs(double x) => Math.Abs(x);
    /// <inheritdoc cref="fabs(double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float fabs(float x) => MathF.Abs(x);
    /// <summary><c>fabsf(x)</c> — explicit single-precision <see cref="fabs(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float fabsf(float x) => MathF.Abs(x);

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
    /// <inheritdoc cref="fmod(double, double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float fmod(float x, float y) => (float)fmod((double)x, (double)y);
    /// <summary><c>fmodf(x, y)</c> — explicit single-precision <see cref="fmod(float, float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float fmodf(float x, float y) => (float)fmod((double)x, (double)y);

    /// <summary><c>fmin(x, y)</c> (C99) — minimum, IEEE-754-aware (treats NaN as "missing").</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double fmin(double x, double y) => Math.Min(x, y);
    /// <inheritdoc cref="fmin(double, double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float fmin(float x, float y) => MathF.Min(x, y);
    /// <summary><c>fminf(x, y)</c> — explicit single-precision <see cref="fmin(float, float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float fminf(float x, float y) => MathF.Min(x, y);

    /// <summary><c>fmax(x, y)</c> (C99) — maximum, IEEE-754-aware.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double fmax(double x, double y) => Math.Max(x, y);
    /// <inheritdoc cref="fmax(double, double)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float fmax(float x, float y) => MathF.Max(x, y);
    /// <summary><c>fmaxf(x, y)</c> — explicit single-precision <see cref="fmax(float, float)"/>.</summary>
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
