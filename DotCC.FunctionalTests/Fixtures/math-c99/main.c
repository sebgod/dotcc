/* The rest of C99 <math.h> beyond the classic set: inverse hyperbolics, exp2,
   expm1/log1p (accurate near zero), logb/ilogb/scalbn, remainder/remquo/fdim,
   fma, the nearest-integer family, nextafter, nan and the classification
   macros. Printed to 12 digits: a platform's libm may differ in the last ulp. */
#include <math.h>
#include <stdio.h>

int main(void) {
    int q;
    printf("%.12g %.12g %.12g %.12g\n", asinh(1.0), acosh(2.0), atanh(0.5), exp2(10.0));
    printf("%.12g %.12g\n", log1p(1e-10) * 1e10, expm1(1e-10) * 1e10);
    printf("%.12g %d %.12g\n", logb(8.0), ilogb(0.75), scalbn(3.0, 4));
    double r = remquo(10.0, 3.0, &q);
    printf("%.12g %d %.12g %.12g\n", r, q, remainder(5.0, 2.0), fdim(1.0, 3.0));
    printf("%.12g %.12g\n", fma(2.0, 3.0, 1.0), (double)fmaf(1.5f, 2.0f, 0.25f));
    printf("%.12g %.12g %ld %ld %lld\n", rint(-2.5), nearbyint(3.5), lround(2.5), lrint(2.5), llround(-2.5));
    printf("%d %d\n", nextafter(1.0, 2.0) > 1.0, nextafter(1.0, 0.0) < 1.0);
    printf("%d %d %d %d %d\n", signbit(-0.0), signbit(1.0), fpclassify(0.0) == FP_ZERO,
           fpclassify(1e-310) == FP_SUBNORMAL, fpclassify(1.0) == FP_NORMAL);
    printf("%d %d %d %d\n", isnormal(1.0), isunordered(1.0, nan("")), isgreater(2.0, 1.0), islessgreater(1.0, 1.0));
    printf("%d %d %d\n", isnan(nan("")), signbit(nan("")), ilogb(0.0) == FP_ILOGB0);
    return 0;
}
