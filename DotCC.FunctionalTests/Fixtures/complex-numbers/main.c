#include <complex.h>
#include <stdio.h>

/* C99 _Complex mapped onto System.Numerics.Complex. The imaginary unit I,
   arithmetic (operators + implicit double conversion), and the <complex.h>
   surface (creal/cimag/cabs/carg/conj). Integer-valued cases so results are
   exact across compilers. */

int main(void) {
    double _Complex z = 3.0 + 4.0 * I;
    double _Complex c = conj(z);
    double _Complex p = z * z;            /* (3+4i)^2 = -7 + 24i */
    double _Complex s = z + 1.0;          /* mixed complex + real */

    printf("%.1f %.1f\n", creal(z), cimag(z));
    printf("%.1f %.1f\n", creal(c), cimag(c));
    printf("%.1f\n", cabs(z));            /* |3+4i| = 5 */
    printf("%.4f\n", carg(z));            /* atan2(4,3) */
    printf("%.1f %.1f\n", creal(p), cimag(p));
    printf("%.1f %.1f\n", creal(s), cimag(s));
    return 0;
}
