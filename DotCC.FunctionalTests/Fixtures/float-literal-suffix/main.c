#include <stdio.h>

/* Float literal suffixes. `f`/`F` → float (passes through to C# verbatim);
   `l`/`L` → C long double, which dotcc maps to C# double (the L is stripped,
   since C# has no float L suffix). Values are exactly representable so they
   print identically across compilers despite long double width differences. */

int main(void) {
    long double a = 1.5L;
    double      b = 2.5l;     /* lowercase long-double suffix */
    float       c = 3.25f;
    double      d = 6.0e2L;   /* suffix after an exponent */
    printf("%.2Lf %.2f %.2f %.1f\n", a, b, c, d);
    return 0;
}
