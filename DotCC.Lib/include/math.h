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

/* INFINITY, NAN and HUGE_VAL. For C# they are declared objects whose bare names resolve to
   CMath's statics through `using static` (typed as CMath types them); the wat target (__wasm__) has no runtime class to resolve them, so they are the constant
   expressions musl spells them as. */
#ifdef __wasm__
#define INFINITY  (1e5000f)
#define NAN       (0.0f/0.0f)
#define HUGE_VALF INFINITY
#define HUGE_VAL  ((double)INFINITY)
#define HUGE_VALL ((long double)INFINITY)
#else
extern const double INFINITY;
extern const double NAN;
extern const double HUGE_VAL;
extern const float HUGE_VALF;
#endif

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

/* Decomposition and manipulation (C99 7.12.6.12, 7.12.7.3, 7.12.11.1). */
double modf(double x, double* iptr);        float modff(float x, float* iptr);
double hypot(double x, double y);           float hypotf(float x, float y);
double copysign(double x, double y);        float copysignf(float x, float y);

/* Classification (C99). Returns int (non-zero on match) — NOT bool. */
int isnan(double x);
int isinf(double x);
int isfinite(double x);

/* The rest of C99 7.12 (DotCC.Libc/MathC99Lib.cs). C's classification and
   comparison macros are functions here, overloaded for float and double in the
   runtime as the ones above are. long double is double in dotcc, so each `l`
   form takes and gives a double. erf, erfc, tgamma and lgamma are not provided
   (no .NET primitive). */
#define FP_NAN       0
#define FP_INFINITE  1
#define FP_ZERO      2
#define FP_SUBNORMAL 3
#define FP_NORMAL    4
#define FP_ILOGB0    (-2147483647 - 1)
#define FP_ILOGBNAN  (-2147483647 - 1)
#define MATH_ERRNO     1
#define MATH_ERREXCEPT 2
#define math_errhandling MATH_ERRNO
typedef double double_t;
typedef float float_t;

int signbit(double x);
int isnormal(double x);
int fpclassify(double x);
int isgreater(double x, double y);
int isgreaterequal(double x, double y);
int isless(double x, double y);
int islessequal(double x, double y);
int islessgreater(double x, double y);
int isunordered(double x, double y);

double acosh(double x);  float acoshf(float x);  long double acoshl(long double x);
double asinh(double x);  float asinhf(float x);  long double asinhl(long double x);
double atanh(double x);  float atanhf(float x);  long double atanhl(long double x);
double exp2(double x);   float exp2f(float x);   long double exp2l(long double x);
double expm1(double x);  float expm1f(float x);  long double expm1l(long double x);
double log1p(double x);  float log1pf(float x);  long double log1pl(long double x);
double logb(double x);   float logbf(float x);   long double logbl(long double x);
int ilogb(double x);     int ilogbf(float x);    int ilogbl(long double x);
double scalbn(double x, int n);   float scalbnf(float x, int n);   long double scalbnl(long double x, int n);
double scalbln(double x, long n); float scalblnf(float x, long n); long double scalblnl(long double x, long n);
double remainder(double x, double y);  float remainderf(float x, float y);  long double remainderl(long double x, long double y);
double remquo(double x, double y, int* quo); float remquof(float x, float y, int* quo); long double remquol(long double x, long double y, int* quo);
double fdim(double x, double y); float fdimf(float x, float y); long double fdiml(long double x, long double y);
double fma(double x, double y, double z); float fmaf(float x, float y, float z); long double fmal(long double x, long double y, long double z);
double rint(double x);      float rintf(float x);      long double rintl(long double x);
double nearbyint(double x); float nearbyintf(float x); long double nearbyintl(long double x);
long lrint(double x);       long lrintf(float x);      long lrintl(long double x);
long long llrint(double x); long long llrintf(float x); long long llrintl(long double x);
long lround(double x);      long lroundf(float x);     long lroundl(long double x);
long long llround(double x); long long llroundf(float x); long long llroundl(long double x);
double nextafter(double x, double y); float nextafterf(float x, float y); long double nextafterl(long double x, long double y);
double nexttoward(double x, long double y); float nexttowardf(float x, long double y); long double nexttowardl(long double x, long double y);
double nan(const char* tagp); float nanf(const char* tagp); long double nanl(const char* tagp);

long double sinl(long double x);   long double cosl(long double x);   long double tanl(long double x);
long double asinl(long double x);  long double acosl(long double x);  long double atanl(long double x);
long double atan2l(long double y, long double x);
long double sinhl(long double x);  long double coshl(long double x);  long double tanhl(long double x);
long double expl(long double x);   long double logl(long double x);
long double log10l(long double x); long double log2l(long double x);
long double powl(long double x, long double y);
long double sqrtl(long double x);  long double cbrtl(long double x);
long double hypotl(long double x, long double y);
long double frexpl(long double x, int* e); long double ldexpl(long double x, int e);
long double modfl(long double x, long double* iptr);
long double ceill(long double x);  long double floorl(long double x);
long double roundl(long double x); long double truncl(long double x);
long double fmodl(long double x, long double y);
long double copysignl(long double x, long double y);
long double fmaxl(long double x, long double y); long double fminl(long double x, long double y);

#endif
