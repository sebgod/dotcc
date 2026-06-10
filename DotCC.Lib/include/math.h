#ifndef _MATH_H
#define _MATH_H

/* dotcc's <math.h> — declares the standard double-precision and explicit
   single-precision ("…f" suffix) function families. The actual
   implementations live in DotCC.Libc.Libc (MathLib.cs) and are inlined
   into every emitted program by Compiler.BuildShell into a CMath
   static class, surfaced via `using static CMath;` — so both
   `sin(double)` and `sin(float)` resolve at the C# overload-resolution
   step. That same overload mechanism is what makes <tgmath.h> work
   without needing C11 _Generic: see tgmath.h. */

/* Math constants. Real C ifdef-gates these under _USE_MATH_DEFINES (on
   MSVC) or always-on (glibc); dotcc just always exposes them. */
#define M_PI    3.14159265358979323846
#define M_E     2.71828182845904523536
#define M_SQRT2 1.41421356237309504880
#define M_LN2   0.6931471805599453
#define M_LN10  2.302585092994046

/* Trigonometric. */
double sin(double x);   float sinf(float x);
double cos(double x);   float cosf(float x);
double tan(double x);   float tanf(float x);
double asin(double x);  float asinf(float x);
double acos(double x);  float acosf(float x);
double atan(double x);  float atanf(float x);
double atan2(double y, double x); float atan2f(float y, float x);

/* Hyperbolic. */
double sinh(double x);  float sinhf(float x);
double cosh(double x);  float coshf(float x);
double tanh(double x);  float tanhf(float x);

/* Exponentials and logarithms. */
double exp(double x);   float expf(float x);
double log(double x);   float logf(float x);
double log10(double x); float log10f(float x);
double log2(double x);  float log2f(float x);

/* Power and roots. */
double pow(double x, double y); float powf(float x, float y);
double sqrt(double x);  float sqrtf(float x);

/* Mantissa / exponent (C90). frexp writes the exponent through its pointer. */
double frexp(double x, int* exp); float frexpf(float x, int* exp);
double ldexp(double x, int exp);  float ldexpf(float x, int exp);
double cbrt(double x);  float cbrtf(float x);

/* Rounding (C99). */
double ceil(double x);  float ceilf(float x);
double floor(double x); float floorf(float x);
double round(double x); float roundf(float x);
double trunc(double x); float truncf(float x);

/* Absolute value, remainder, min/max (C99 for fmin/fmax). The `l` variant is
   the 64-bit `long double` dotcc models (= double). */
double fabs(double x);          float fabsf(float x);
long double fabsl(long double x);
double fmod(double x, double y); float fmodf(float x, float y);
double fmin(double x, double y); float fminf(float x, float y);
double fmax(double x, double y); float fmaxf(float x, float y);

/* Classification (C99). Returns int (non-zero on match) — NOT bool. */
int isnan(double x);
int isinf(double x);
int isfinite(double x);

#endif
