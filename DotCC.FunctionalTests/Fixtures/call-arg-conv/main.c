/* C converts each argument to its parameter type at the call (the prototype is
 * in scope) — int -> unsigned/size_t, enum -> int, a wider value -> a narrower
 * param. C# rejects those implicit conversions, so dotcc records each function's
 * parameter types and coerces the arguments to them (the call-site twin of the
 * store conversions). An out-of-range constant argument uses unchecked. */
#include <stdio.h>
#include <stddef.h>

enum Color { RED, GREEN, BLUE };

static unsigned long area(size_t w, size_t h) { return w * h; }  /* int args -> size_t params */
static int           shade(int c)             { return c * 10; } /* enum arg -> int param */
static int           low8(unsigned char b)    { return b; }      /* int arg -> byte param (narrowing) */

int main(void) {
    int w = 4, h = 5;
    unsigned long a = area(w, h);   /* area((size_t)4, (size_t)5) = 20 */
    int s = shade(BLUE);            /* shade((int)BLUE) = shade(2) = 20 */
    int b = low8(0x141);            /* low8(unchecked((byte)0x141)): 0x141 & 0xFF = 65 */

    printf("%lu %d %d\n", a, s, b); /* 20 20 65 */
    return 0;
}
