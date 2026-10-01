/* cabs: the magnitude, by the C# runtime's System.Numerics.Complex.Abs (its scaled hypot), so the two targets agree. The wat target's libc (WatLibc), compiled with the program. */
#include <complex.h>
#include <math.h>

double cabs(double _Complex z)
{
    double a = fabs(((double *)&z)[0]);
    double b = fabs(((double *)&z)[1]);
    double small = a < b ? a : b;
    double large = a < b ? b : a;
    if (small == 0.0) { return large; }
    if (isinf(large) && !isnan(small)) { return large; }
    double ratio = small / large;
    return large * sqrt(1.0 + ratio * ratio);
}
