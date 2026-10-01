/* conj: the complex conjugate, the imaginary part negated. The wat target's libc (WatLibc), compiled with the program. */
#include <complex.h>
#include <math.h>

double _Complex conj(double _Complex z)
{
    double _Complex r = z;
    ((double *)&r)[1] = -((double *)&r)[1];
    return r;
}
