#ifndef _FLOAT_H
#define _FLOAT_H

/* dotcc's <float.h> — C99 5.2.4.2.2. Floating-point limits for
   IEEE-754 binary32 (`float`) and binary64 (`double`). Values
   below are the IEEE-754 / C99 standard constants and match what
   glibc / MSVC ship.

   dotcc has no `long double` distinct from `double` (the C# / .NET
   runtime uses IEEE-754 double for both). LDBL_* macros alias the
   DBL_* values rather than reflecting the x87 80-bit extended type
   some C compilers expose. */

/* Radix of the floating-point representation. IEEE-754 = base 2. */
#define FLT_RADIX  2

/* Number of base-FLT_RADIX digits in the significand (mantissa). */
#define FLT_MANT_DIG  24
#define DBL_MANT_DIG  53
#define LDBL_MANT_DIG 53

/* Number of decimal digits, q, such that any floating-point number
   with q decimal digits can be rounded and converted back without
   loss. */
#define FLT_DIG  6
#define DBL_DIG  15
#define LDBL_DIG 15

/* Minimum / maximum integer exponent. */
#define FLT_MIN_EXP   (-125)
#define FLT_MAX_EXP   128
#define DBL_MIN_EXP   (-1021)
#define DBL_MAX_EXP   1024
#define LDBL_MIN_EXP  (-1021)
#define LDBL_MAX_EXP  1024

/* Minimum / maximum decimal exponent — smallest / largest 10^N
   representable as a normalized value. */
#define FLT_MIN_10_EXP   (-37)
#define FLT_MAX_10_EXP   38
#define DBL_MIN_10_EXP   (-307)
#define DBL_MAX_10_EXP   308
#define LDBL_MIN_10_EXP  (-307)
#define LDBL_MAX_10_EXP  308

/* Maximum representable finite value. */
#define FLT_MAX   3.402823466e+38F
#define DBL_MAX   1.7976931348623157e+308
#define LDBL_MAX  1.7976931348623157e+308

/* Smallest positive normalized value. */
#define FLT_MIN   1.175494351e-38F
#define DBL_MIN   2.2250738585072014e-308
#define LDBL_MIN  2.2250738585072014e-308

/* Smallest value such that 1.0 + EPSILON != 1.0 — the gap between
   1.0 and the next representable value. */
#define FLT_EPSILON   1.192092896e-07F
#define DBL_EPSILON   2.2204460492503131e-16
#define LDBL_EPSILON  2.2204460492503131e-16

/* Rounding mode. 1 = round to nearest (IEEE-754 default).
   .NET's FP unit always rounds to nearest-even. */
#define FLT_ROUNDS  1

/* Evaluation method. 0 = each operation evaluates to its declared
   type, no extra precision. .NET's stack-VM behaviour matches. */
#define FLT_EVAL_METHOD  0

#endif
