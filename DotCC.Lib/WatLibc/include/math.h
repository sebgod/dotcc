#ifndef _MATH_H
#define _MATH_H

/* <math.h> as the wat target's libc sees it (a program sees DotCC.Lib/include/math.h).
   The declarations are the public header's, the same prototypes, plus what musl's own
   <math.h> gives its sources and dotcc's public one does not: double_t and float_t,
   INFINITY and NAN, and the classification macros over the bits. Adapted from musl's
   include/math.h (MIT, see musl/COPYRIGHT). */

typedef double double_t;
typedef float float_t;

#define NAN       (0.0f/0.0f)
#define INFINITY  (1e5000f)
#define HUGE_VALF INFINITY
#define HUGE_VAL  ((double)INFINITY)

#define M_E        2.7182818284590452354
#define M_LN2      0.69314718055994530942
#define M_LN10     2.30258509299404568402
#define M_PI       3.14159265358979323846
#define M_PI_2     1.57079632679489661923
#define M_PI_4     0.78539816339744830962
#define M_SQRT2    1.41421356237309504880

static inline unsigned __FLOAT_BITS(float __f)
{
	union {float __f; unsigned __i;} __u;
	__u.__f = __f;
	return __u.__i;
}

static inline unsigned long long __DOUBLE_BITS(double __f)
{
	union {double __f; unsigned long long __i;} __u;
	__u.__f = __f;
	return __u.__i;
}

#define isnan(x) ( \
	sizeof(x) == sizeof(float) ? (__FLOAT_BITS(x) & 0x7fffffff) > 0x7f800000 : \
	(__DOUBLE_BITS(x) & -1ULL>>1) > 0x7ffULL<<52)

#define isinf(x) ( \
	sizeof(x) == sizeof(float) ? (__FLOAT_BITS(x) & 0x7fffffff) == 0x7f800000 : \
	(__DOUBLE_BITS(x) & -1ULL>>1) == 0x7ffULL<<52)

#define isfinite(x) ( \
	sizeof(x) == sizeof(float) ? (__FLOAT_BITS(x) & 0x7fffffff) < 0x7f800000 : \
	(__DOUBLE_BITS(x) & -1ULL>>1) < 0x7ffULL<<52)

#define signbit(x) ( \
	sizeof(x) == sizeof(float) ? (int)(__FLOAT_BITS(x)>>31) : \
	(int)(__DOUBLE_BITS(x)>>63))

double sin(double x);   float sinf(float x);
double cos(double x);   float cosf(float x);
double tan(double x);   float tanf(float x);
double asin(double x);  float asinf(float x);
double acos(double x);  float acosf(float x);
double atan(double x);  float atanf(float x);
double atan2(double y, double x); float atan2f(float y, float x);

double sinh(double x);  float sinhf(float x);
double cosh(double x);  float coshf(float x);
double tanh(double x);  float tanhf(float x);

double exp(double x);   float expf(float x);
double expm1(double x); float expm1f(float x);
double log(double x);   float logf(float x);
double log10(double x); float log10f(float x);
double log2(double x);  float log2f(float x);

double pow(double x, double y); float powf(float x, float y);
double sqrt(double x);  float sqrtf(float x);

double frexp(double x, int* exp); float frexpf(float x, int* exp);
double ldexp(double x, int exp);  float ldexpf(float x, int exp);
double scalbn(double x, int n);   float scalbnf(float x, int n);
double cbrt(double x);  float cbrtf(float x);

double ceil(double x);  float ceilf(float x);
double floor(double x); float floorf(float x);
double round(double x); float roundf(float x);
double trunc(double x); float truncf(float x);

double fabs(double x);          float fabsf(float x);
long double fabsl(long double x);
double fmod(double x, double y); float fmodf(float x, float y);
double fmin(double x, double y); float fminf(float x, float y);
double fmax(double x, double y); float fmaxf(float x, float y);

double modf(double x, double* iptr);        float modff(float x, float* iptr);
double hypot(double x, double y);           float hypotf(float x, float y);
double copysign(double x, double y);        float copysignf(float x, float y);

#endif
