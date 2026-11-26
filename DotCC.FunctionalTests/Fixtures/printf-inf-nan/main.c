/* Infinities and NaNs under %f %e %g %a and their uppercase forms (C11
   7.21.6.1p8): inf/nan, INF/NAN, the precision ignored, the field width kept
   and padded with spaces whatever the 0 flag, a negative NaN's sign printed as
   glibc prints it. C's NAN has its sign bit clear. */
#include <math.h>
#include <stdio.h>

int main(void) {
    double inf = INFINITY, n = NAN;
    printf("[%f] [%f] [%.3f] [%F]\n", inf, -inf, n, inf);
    printf("[%e] [%E] [%g] [%G]\n", inf, -inf, n, -n);
    printf("[%8f] [%-8f|] [%+f] [% f] [%a]\n", inf, inf, inf, inf, inf);
    printf("[%08f] [%05.1f] [%d]\n", inf, -n, signbit(n));
    char b[32];
    snprintf(b, sizeof b, "%g/%f", -inf, n);
    printf("[%s]\n", b);
    return 0;
}
