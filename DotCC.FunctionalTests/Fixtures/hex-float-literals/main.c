#include <stdio.h>

/* C99 hex float literals — `0xH.HpE`, value = (hex mantissa) * 2^exponent.
   C# has no hex-float syntax, so dotcc parses the value and emits a decimal
   double/float. All values here are exactly representable, so they match a
   real compiler bit-for-bit. */

int main(void) {
    double a = 0x1.8p3;     /* 1.5 * 2^3   = 12.0 */
    double b = 0x1p-1;      /* 1.0 * 2^-1  = 0.5  */
    double c = 0x.8p1;      /* 0.5 * 2^1   = 1.0  */
    float  d = 0x1.4p2f;    /* 1.25 * 2^2  = 5.0  */
    double e = 0x1.921p0;   /* ~pi-ish, exact in binary */
    printf("%.4f %.4f %.4f %.4f %.6f\n", a, b, c, d, e);
    return 0;
}
