/* `long double` factorial. dotcc lowers `long double` → C# `double` (the CLI's
 * widest IEEE float, mirroring `long long` → `long`); the `%Lf` spec routes a
 * double arg through the same path with the length modifier ignored. Values
 * stay double-exact through 22! (21!/22! exceed 64-bit `unsigned long long`),
 * so gcc — whose aarch64 `long double` is binary128 — produces byte-identical
 * output here. 23! onward would diverge (binary128 stays exact, double rounds),
 * which is why this stops at 22. */
#include <stdio.h>

int main(void) {
    long double f = 1;
    for (int i = 1; i <= 22; i++) {
        f *= i;
        printf("%2d! = %.0Lf\n", i, f);
    }
    return 0;
}
