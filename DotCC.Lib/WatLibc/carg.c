/* carg: the phase, atan2 of the imaginary and real parts. The wat target's libc (WatLibc), compiled with the program. */
#include <complex.h>
#include <math.h>

double carg(double _Complex z)
{
    return atan2(((double *)&z)[1], ((double *)&z)[0]);
}
