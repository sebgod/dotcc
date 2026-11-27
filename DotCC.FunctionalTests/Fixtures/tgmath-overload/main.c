/* Type-generic math via <tgmath.h>. dotcc's header is real C11 _Generic
   macros, resolved at lowering time: sqrt(1.0f) is sqrtf and has type float,
   sqrt(1) is sqrt (an integer counts as double), sqrt(1.0L) is sqrtl, and
   pow(1.0f, 2) is pow while pow(1.0f, 2.0f) is powf (C11 7.25p3). The
   numeric rows use round-trip-safe inputs so float and double agree at
   "%.6f". The complex rows are in tgmath-complex (wat has no _Complex). */

#define _USE_MATH_DEFINES

#include <stdio.h>
#include <tgmath.h>

int main(void)
{
    /* Double-precision dispatch. */
    double d = 0.5;
    printf("sin(0.5d) = %.6f\n", sin(d));
    printf("cos(0.5d) = %.6f\n", cos(d));
    printf("sqrt(2d)  = %.6f\n", sqrt(2.0));

    /* Single-precision dispatch via explicit ...f functions (works
       across compilers: dotcc routes them to MathF.X, MSVC to the
       float-typed C math runtime). */
    printf("sqrtf(4f) = %.6f\n", sqrtf(4.0f));
    printf("sinf(0f)  = %.6f\n", sinf(0.0f));
    printf("cosf(0f)  = %.6f\n", cosf(0.0f));

    /* Which function was chosen shows in the type of the call. */
    printf("sqrt(1.0f) float: %d\n", _Generic(sqrt(1.0f), float: 1, default: 0));
    printf("sqrt(1) double: %d\n", _Generic(sqrt(1), double: 1, default: 0));
    printf("sqrt(1.0L) long double: %d\n", _Generic(sqrt(1.0L), long double: 1, default: 0));
    printf("pow(1.0f, 2) double: %d\n", _Generic(pow(1.0f, 2), double: 1, default: 0));
    printf("pow(1.0f, 2.0f) float: %d\n", _Generic(pow(1.0f, 2.0f), float: 1, default: 0));
    printf("sqrt(4f) = %.6f\n", sqrt(4.0f));

    return 0;
}
