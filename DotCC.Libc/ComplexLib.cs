#nullable enable

using System.Numerics;
using System.Runtime.CompilerServices;

namespace DotCC.Libc;

/// <summary>
/// C99 <c>&lt;complex.h&gt;</c> surface, mapped onto .NET's
/// <see cref="System.Numerics.Complex"/> (double-backed). dotcc lowers every C
/// complex type — <c>float _Complex</c> / <c>double _Complex</c> / <c>long
/// double _Complex</c> — to <see cref="Complex"/>, so the <c>float</c> / <c>long
/// double</c> variants widen to double precision (a documented narrowing, the
/// same shape as the <c>long double</c> → <c>double</c> mapping elsewhere).
/// </summary>
/// <remarks>
/// Arithmetic needs nothing special from the emitter: <see cref="Complex"/>
/// supplies the <c>+ - * /</c> operators and an implicit conversion from
/// <c>double</c>, so <c>z + 1.0</c> and <c>2.0 * I</c> compile directly. The
/// imaginary unit <c>I</c> (and <c>_Complex_I</c>) is a <c>&lt;complex.h&gt;</c>
/// macro for <see cref="__dotcc_complex_I"/>. v1 provides the double-precision
/// functions; the explicit <c>…f</c> (float) / <c>…l</c> (long double) variants
/// aren't declared yet (they'd alias these, since all map to <see cref="Complex"/>).
/// </remarks>
public static unsafe partial class Libc
{
    /// <summary>The imaginary unit — <c>I</c> / <c>_Complex_I</c> in &lt;complex.h&gt;.</summary>
    public static Complex __dotcc_complex_I => Complex.ImaginaryOne;

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static double creal(Complex z) => z.Real;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static double cimag(Complex z) => z.Imaginary;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static double cabs(Complex z) => Complex.Abs(z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static double carg(Complex z) => z.Phase;

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Complex conj(Complex z) => Complex.Conjugate(z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Complex cproj(Complex z) =>
        // Projection onto the Riemann sphere: a finite value maps to itself; any
        // infinity maps to (+inf, ±0) keeping the sign of the imaginary part.
        double.IsInfinity(z.Real) || double.IsInfinity(z.Imaginary)
            ? new Complex(double.PositiveInfinity, z.Imaginary < 0 ? -0.0 : 0.0)
            : z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Complex csqrt(Complex z) => Complex.Sqrt(z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Complex cexp(Complex z) => Complex.Exp(z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Complex clog(Complex z) => Complex.Log(z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Complex cpow(Complex x, Complex y) => Complex.Pow(x, y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Complex csin(Complex z) => Complex.Sin(z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Complex ccos(Complex z) => Complex.Cos(z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Complex ctan(Complex z) => Complex.Tan(z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Complex casin(Complex z) => Complex.Asin(z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Complex cacos(Complex z) => Complex.Acos(z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Complex catan(Complex z) => Complex.Atan(z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Complex csinh(Complex z) => Complex.Sinh(z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Complex ccosh(Complex z) => Complex.Cosh(z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Complex ctanh(Complex z) => Complex.Tanh(z);
}
