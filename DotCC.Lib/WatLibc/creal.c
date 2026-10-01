/* creal: the real part, a double _Complex's first double. The wat target's libc (WatLibc), compiled with the program. */
#include <complex.h>
#include <math.h>

double creal(double _Complex z)
{
    return ((double *)&z)[0];
}
