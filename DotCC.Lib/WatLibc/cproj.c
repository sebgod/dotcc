/* cproj: the projection onto the Riemann sphere: a finite value is itself, an infinite one (+inf, 0 with the imaginary part's sign). The wat target's libc (WatLibc), compiled with the program. */
#include <complex.h>
#include <math.h>

double _Complex cproj(double _Complex z)
{
    double re = ((double *)&z)[0], im = ((double *)&z)[1];
    if (!isinf(re) && !isinf(im)) { return z; }
    double _Complex r = z;
    ((double *)&r)[0] = INFINITY;
    ((double *)&r)[1] = im < 0 ? -0.0 : 0.0;
    return r;
}
