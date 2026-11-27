#ifndef _TGMATH_H
#define _TGMATH_H

/* dotcc's <tgmath.h> (C11 7.25): type-generic math as _Generic macros, which
   dotcc resolves at lowering time. Each macro selects the function itself
   (a generic selection in callee position is a function designator, C11
   6.5.1.1p4), so the call is typed from the selected prototype: sqrt(1.0f)
   is sqrtf and has type float, sqrt(1) is sqrt, sqrt(1.0L) is sqrtl, and a
   complex argument calls the c... function.

   Three groups, as in 7.25p4 and p5:
   - Real and complex (acos asin atan acosh asinh atanh cos sin tan cosh sinh
     tanh exp log pow sqrt, and fabs which goes to cabs): a complex argument
     calls the c... function.
   - Real only (everything else math.h declares that takes a floating
     argument, except erf, erfc, lgamma and tgamma, which wait for #344).
   - Complex only (carg cimag conj cproj creal): the plain functions. The
     crealf family waits for #190 and #210.

   There is ONE complex arm, `double _Complex`, because dotcc lowers every
   _Complex width to one type; per-width arms come with #190.

   Several-argument macros select on the usual arithmetic conversions of the
   generic arguments (7.25p3), where an integer counts as double: __TGM maps
   an argument to itself when it is float, long double or complex and to 0.0
   otherwise, and the sum is only ever the unevaluated controlling
   expression. Only the generic parameters take part: the int* of frexp and
   remquo, the int of ldexp, scalbn and scalbln, and the long double of
   nexttoward do not. */

#include <math.h>
#include <complex.h>

#define __TGM(x) _Generic((x), float: (x), long double: (x), double _Complex: (x), default: 0.0)

#define acos(x) _Generic((x), float: acosf, long double: acosl, double _Complex: cacos, default: acos)(x)
#define asin(x) _Generic((x), float: asinf, long double: asinl, double _Complex: casin, default: asin)(x)
#define atan(x) _Generic((x), float: atanf, long double: atanl, double _Complex: catan, default: atan)(x)
#define acosh(x) _Generic((x), float: acoshf, long double: acoshl, double _Complex: cacosh, default: acosh)(x)
#define asinh(x) _Generic((x), float: asinhf, long double: asinhl, double _Complex: casinh, default: asinh)(x)
#define atanh(x) _Generic((x), float: atanhf, long double: atanhl, double _Complex: catanh, default: atanh)(x)
#define cos(x) _Generic((x), float: cosf, long double: cosl, double _Complex: ccos, default: cos)(x)
#define sin(x) _Generic((x), float: sinf, long double: sinl, double _Complex: csin, default: sin)(x)
#define tan(x) _Generic((x), float: tanf, long double: tanl, double _Complex: ctan, default: tan)(x)
#define cosh(x) _Generic((x), float: coshf, long double: coshl, double _Complex: ccosh, default: cosh)(x)
#define sinh(x) _Generic((x), float: sinhf, long double: sinhl, double _Complex: csinh, default: sinh)(x)
#define tanh(x) _Generic((x), float: tanhf, long double: tanhl, double _Complex: ctanh, default: tanh)(x)
#define exp(x) _Generic((x), float: expf, long double: expl, double _Complex: cexp, default: exp)(x)
#define log(x) _Generic((x), float: logf, long double: logl, double _Complex: clog, default: log)(x)
#define sqrt(x) _Generic((x), float: sqrtf, long double: sqrtl, double _Complex: csqrt, default: sqrt)(x)
#define fabs(x) _Generic((x), float: fabsf, long double: fabsl, double _Complex: cabs, default: fabs)(x)
#define cbrt(x) _Generic((x), float: cbrtf, long double: cbrtl, default: cbrt)(x)
#define ceil(x) _Generic((x), float: ceilf, long double: ceill, default: ceil)(x)
#define floor(x) _Generic((x), float: floorf, long double: floorl, default: floor)(x)
#define exp2(x) _Generic((x), float: exp2f, long double: exp2l, default: exp2)(x)
#define expm1(x) _Generic((x), float: expm1f, long double: expm1l, default: expm1)(x)
#define log10(x) _Generic((x), float: log10f, long double: log10l, default: log10)(x)
#define log1p(x) _Generic((x), float: log1pf, long double: log1pl, default: log1p)(x)
#define log2(x) _Generic((x), float: log2f, long double: log2l, default: log2)(x)
#define logb(x) _Generic((x), float: logbf, long double: logbl, default: logb)(x)
#define ilogb(x) _Generic((x), float: ilogbf, long double: ilogbl, default: ilogb)(x)
#define lrint(x) _Generic((x), float: lrintf, long double: lrintl, default: lrint)(x)
#define llrint(x) _Generic((x), float: llrintf, long double: llrintl, default: llrint)(x)
#define lround(x) _Generic((x), float: lroundf, long double: lroundl, default: lround)(x)
#define llround(x) _Generic((x), float: llroundf, long double: llroundl, default: llround)(x)
#define nearbyint(x) _Generic((x), float: nearbyintf, long double: nearbyintl, default: nearbyint)(x)
#define rint(x) _Generic((x), float: rintf, long double: rintl, default: rint)(x)
#define round(x) _Generic((x), float: roundf, long double: roundl, default: round)(x)
#define trunc(x) _Generic((x), float: truncf, long double: truncl, default: trunc)(x)
#define frexp(x, p) _Generic((x), float: frexpf, long double: frexpl, default: frexp)((x), (p))
#define modf(x, p) _Generic((x), float: modff, long double: modfl, default: modf)((x), (p))
#define ldexp(x, y) _Generic((x), float: ldexpf, long double: ldexpl, default: ldexp)((x), (y))
#define scalbn(x, y) _Generic((x), float: scalbnf, long double: scalbnl, default: scalbn)((x), (y))
#define scalbln(x, y) _Generic((x), float: scalblnf, long double: scalblnl, default: scalbln)((x), (y))
#define nexttoward(x, y) _Generic((x), float: nexttowardf, long double: nexttowardl, default: nexttoward)((x), (y))
#define pow(x, y) _Generic(__TGM(x) + __TGM(y), float: powf, long double: powl, double _Complex: cpow, default: pow)((x), (y))
#define atan2(x, y) _Generic(__TGM(x) + __TGM(y), float: atan2f, long double: atan2l, default: atan2)((x), (y))
#define copysign(x, y) _Generic(__TGM(x) + __TGM(y), float: copysignf, long double: copysignl, default: copysign)((x), (y))
#define fdim(x, y) _Generic(__TGM(x) + __TGM(y), float: fdimf, long double: fdiml, default: fdim)((x), (y))
#define fmax(x, y) _Generic(__TGM(x) + __TGM(y), float: fmaxf, long double: fmaxl, default: fmax)((x), (y))
#define fmin(x, y) _Generic(__TGM(x) + __TGM(y), float: fminf, long double: fminl, default: fmin)((x), (y))
#define fmod(x, y) _Generic(__TGM(x) + __TGM(y), float: fmodf, long double: fmodl, default: fmod)((x), (y))
#define hypot(x, y) _Generic(__TGM(x) + __TGM(y), float: hypotf, long double: hypotl, default: hypot)((x), (y))
#define nextafter(x, y) _Generic(__TGM(x) + __TGM(y), float: nextafterf, long double: nextafterl, default: nextafter)((x), (y))
#define remainder(x, y) _Generic(__TGM(x) + __TGM(y), float: remainderf, long double: remainderl, default: remainder)((x), (y))
#define remquo(x, y, q) _Generic(__TGM(x) + __TGM(y), float: remquof, long double: remquol, default: remquo)((x), (y), (q))
#define fma(x, y, z) _Generic(__TGM(x) + __TGM(y) + __TGM(z), float: fmaf, long double: fmal, default: fma)((x), (y), (z))

#endif
