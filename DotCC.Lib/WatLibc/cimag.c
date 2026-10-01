/* cimag: the imaginary part, a double _Complex's second double. The wat target's libc (WatLibc), compiled with the program. */
#include <complex.h>
#include <math.h>

double cimag(double _Complex z)
{
    return ((double *)&z)[1];
}
