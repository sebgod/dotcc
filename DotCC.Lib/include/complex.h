#ifndef _COMPLEX_H
#define _COMPLEX_H

/* dotcc's <complex.h> — C99 complex numbers mapped onto .NET's
   System.Numerics.Complex (double-backed). The `_Complex` type keyword lowers
   to System.Numerics.Complex; the implementations below live in
   DotCC.Libc.Libc (ComplexLib.cs) and are inlined into every emitted program,
   surfaced via `using static Libc;`. Arithmetic uses Complex's own operators
   (and its implicit conversion from double), so `z + 1.0`, `2.0 * I`, etc.
   work directly with nothing emitted.

   float/long-double complex variants widen to double precision (documented). */

/* `complex` is a convenience macro for the `_Complex` keyword (C99 7.3.1). */
#define complex _Complex

/* The imaginary unit. `_Complex_I` is the value; `I` is the usual spelling. */
#define _Complex_I __dotcc_complex_I
#define I _Complex_I

/* Real / imaginary parts and magnitude / argument (return double). */
double creal(double _Complex z);
double cimag(double _Complex z);
double cabs(double _Complex z);
double carg(double _Complex z);

/* Algebraic. */
double _Complex conj(double _Complex z);
double _Complex cproj(double _Complex z);
double _Complex csqrt(double _Complex z);
double _Complex cpow(double _Complex x, double _Complex y);

/* Exponential / logarithmic. */
double _Complex cexp(double _Complex z);
double _Complex clog(double _Complex z);

/* Trigonometric. */
double _Complex csin(double _Complex z);
double _Complex ccos(double _Complex z);
double _Complex ctan(double _Complex z);
double _Complex casin(double _Complex z);
double _Complex cacos(double _Complex z);
double _Complex catan(double _Complex z);

/* Hyperbolic. */
double _Complex csinh(double _Complex z);
double _Complex ccosh(double _Complex z);
double _Complex ctanh(double _Complex z);

#endif /* _COMPLEX_H */
