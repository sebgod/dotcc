/* strtod / atof: parse leading floating-point numbers from C strings, with the
 * endptr reporting where parsing stopped (so a buffer of several numbers can be
 * walked). Trailing junk and leading whitespace/sign/exponent are handled. */
#include <stdio.h>
#include <stdlib.h>
#include <stddef.h>   /* NULL */

int main(void) {
    printf("%g\n", strtod("3.14", NULL));          /* 3.14 */
    printf("%g\n", strtod("  -2.5e3xyz", NULL));   /* -2500 (stops at 'x') */
    printf("%g\n", atof("42"));                    /* 42 */

    /* endptr walk: two numbers out of one buffer */
    const char *s = "1.5 2.25";
    char *end;
    double a = strtod(s, &end);
    double b = strtod(end, &end);
    printf("%g %g\n", a, b);                        /* 1.5 2.25 */
    return 0;
}
