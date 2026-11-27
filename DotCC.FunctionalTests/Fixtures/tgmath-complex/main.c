/* <tgmath.h> with complex arguments (C11 7.25): sqrt(z) is csqrt(z), fabs(z)
   is cabs(z), pow(z, 2.0) is cpow. Inputs are exact-valued and the output
   precision hides last-ulp libm differences. */

#include <stdio.h>
#include <tgmath.h>

int main(void)
{
    double _Complex z = 4.0;
    double _Complex r = sqrt(z);
    printf("sqrt(4) = %.4f %.4f\n", creal(r), cimag(r));

    double _Complex m = -4.0;
    double _Complex q = sqrt(m);
    printf("sqrt(-4) = %.4f %.4f\n", creal(q), cimag(q));

    double _Complex zero = 0.0;
    double _Complex e = exp(zero);
    printf("exp(0) = %.4f %.4f\n", creal(e), cimag(e));

    double _Complex one = 1.0;
    double _Complex l = log(one);
    printf("log(1) = %.4f %.4f\n", creal(l), cimag(l));

    double _Complex s = sin(zero);
    printf("sin(0) = %.4f %.4f\n", creal(s), cimag(s));

    double _Complex two = 2.0 + 1.0 * I;
    double _Complex p = pow(two, 2.0);
    printf("pow(2+i, 2) = %.4f %.4f\n", creal(p), cimag(p));

    double _Complex t = 3.0 + 4.0 * I;
    double a = fabs(t);
    printf("fabs(3+4i) = %.4f\n", a);

    double _Complex h = acosh(one);
    printf("acosh(1) = %.4f %.4f\n", creal(h), cimag(h));

    printf("type sqrt(z): %d\n", _Generic(sqrt(z), double _Complex: 1, default: 0));
    printf("type fabs(z): %d\n", _Generic(fabs(z), double: 1, default: 0));
    printf("long double sum: %d\n", _Generic(1.0 + 1.0L, long double: 1, default: 0));
    return 0;
}
