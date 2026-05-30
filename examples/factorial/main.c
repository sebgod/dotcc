/* Factorial in `long double`. On the CLI (dotcc's target), `long double`
 * lowers to C# `double` — the widest IEEE float the runtime offers — exactly
 * as C's `long long` lowers to C# `long`. A 64-bit `unsigned long long`
 * overflows at 21! (20! = 2.4e18 is the last that fits); the floating type
 * carries the value past that, staying EXACT through 22! (every line below is
 * the true integer). 23! onward would round — for arbitrary extended range use
 * `_Float128`, dotcc's true software binary128.
 *
 * (On native targets `long double` width is ABI-specific — 80-bit on x86,
 * 128-bit on aarch64, 64-bit on MSVC — but the CLI has no such ambiguity.)
 *
 * Build + run:
 *   dotnet run --project DotCC -c Release -- --emit=csharp examples/factorial/main.c > fact.cs
 *   dotnet run fact.cs
 */
#include <stdio.h>

int main(void) {
    long double f = 1;
    for (int i = 1; i <= 22; i++) {
        f *= i;
        printf("%2d! = %.0Lf\n", i, f);   /* %Lf is the required spec for long double */
    }
    return 0;
}
