/* Type-generic math dispatch via <tgmath.h>. In real C this is C11
   _Generic; in dotcc the same effect falls out of C# overload
   resolution because the CMath helper class has both `sin(float)` and
   `sin(double)` overloads. Compile this with MSVC's oracle and it
   should still work (MSVC's tgmath.h provides the real _Generic
   macros). The expected output below comes from the double-precision
   path on both sides — values match because we use round-trip-safe
   inputs (0.5 has an exact float representation, and printf's "%.6f"
   truncates well before the float/double divergence point). */

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

    return 0;
}
