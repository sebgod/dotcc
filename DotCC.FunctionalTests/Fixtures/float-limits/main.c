/* <float.h> — IEEE-754 limit macros. Integer-valued macros are
   printed directly; floating-point limits are compared against a
   threshold so the output is byte-stable across compilers (avoids
   small representation differences in scientific-notation print). */

#include <stdio.h>
#include <float.h>

int main(void)
{
    /* Integer constants — exact byte-stable across compilers. */
    printf("FLT_RADIX=%d FLT_ROUNDS=%d FLT_EVAL_METHOD=%d\n",
        FLT_RADIX, FLT_ROUNDS, FLT_EVAL_METHOD);
    printf("FLT_DIG=%d DBL_DIG=%d LDBL_DIG=%d\n",
        FLT_DIG, DBL_DIG, LDBL_DIG);
    printf("FLT_MANT_DIG=%d DBL_MANT_DIG=%d\n",
        FLT_MANT_DIG, DBL_MANT_DIG);

    /* Exponent bounds — also integer-valued. */
    printf("FLT_MIN_EXP=%d FLT_MAX_EXP=%d\n", FLT_MIN_EXP, FLT_MAX_EXP);
    printf("DBL_MIN_EXP=%d DBL_MAX_EXP=%d\n", DBL_MIN_EXP, DBL_MAX_EXP);

    /* Decimal exponent bounds. */
    printf("FLT_MIN_10_EXP=%d FLT_MAX_10_EXP=%d\n",
        FLT_MIN_10_EXP, FLT_MAX_10_EXP);
    printf("DBL_MIN_10_EXP=%d DBL_MAX_10_EXP=%d\n",
        DBL_MIN_10_EXP, DBL_MAX_10_EXP);

    /* Floating-point limits — sanity-check the values are in the
       expected ranges rather than printing full precision (which can
       differ across implementations). */
    printf("FLT_MAX > 1e38: %d\n",     FLT_MAX > 1.0e38 ? 1 : 0);
    printf("DBL_MAX > 1e300: %d\n",    DBL_MAX > 1.0e300 ? 1 : 0);
    printf("FLT_MIN < 1e-37: %d\n",    FLT_MIN < 1.0e-37 ? 1 : 0);
    printf("DBL_MIN < 1e-307: %d\n",   DBL_MIN < 1.0e-307 ? 1 : 0);
    printf("FLT_EPSILON < 1e-6: %d\n", FLT_EPSILON < 1.0e-6 ? 1 : 0);
    printf("DBL_EPSILON < 1e-15: %d\n", DBL_EPSILON < 1.0e-15 ? 1 : 0);

    return 0;
}
