/* Smoke test for the core <math.h> surface: a handful of double-precision
   functions exercised against well-known values. The M_PI / M_E
   constants are validated separately in LibcMathTests — they're gated
   under _USE_MATH_DEFINES on MSVC and the per-compiler quirk noise isn't
   worth bringing into a fixture. Output formats are chosen so the MSVC
   oracle and dotcc agree byte-for-byte (no transcendental boundary
   cases; inputs round-trip exactly through float/double). */

#include <stdio.h>
#include <math.h>

int main(void)
{
    /* Roots and powers. */
    printf("sqrt(2)   = %.6f\n", sqrt(2.0));
    printf("cbrt(27)  = %.6f\n", cbrt(27.0));
    printf("pow(2,10) = %.6f\n", pow(2.0, 10.0));

    /* Logs / exponentials at exact-result points. log10(100) and log2(8)
       are integer-valued so they're identical across implementations. */
    printf("log10(100)= %.6f\n", log10(100.0));
    printf("log2(8)   = %.6f\n", log2(8.0));
    printf("exp(0)    = %.6f\n", exp(0.0));

    /* Rounding. */
    printf("ceil(3.2) = %.6f\n", ceil(3.2));
    printf("floor(3.8)= %.6f\n", floor(3.8));
    printf("trunc(3.9)= %.6f\n", trunc(3.9));
    printf("round(0.5)= %.6f\n", round(0.5));

    /* Abs / min / max. */
    printf("fabs(-5.5)= %.6f\n", fabs(-5.5));
    printf("fmin(3,5) = %.6f\n", fmin(3.0, 5.0));
    printf("fmax(3,5) = %.6f\n", fmax(3.0, 5.0));

    return 0;
}
