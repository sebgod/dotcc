/* C's usual arithmetic conversions (§6.3.1.8): when a binary operator mixes a
 * signed integer with a 64-bit unsigned (size_t / unsigned long), the signed
 * operand is converted to the unsigned type. dotcc lowers size_t -> C# ulong;
 * C# refuses to unify ulong with a signed int (CS0034), so dotcc inserts the C
 * conversion cast and tags the result with the common (unsigned) type, which
 * then propagates up nested expressions and into comparisons / stores. This is
 * Lua's `MAX_SIZET / sizeof(T)` array-cap idiom, unsigned masks, and mixed
 * signed/unsigned comparisons. */
#include <stdio.h>
#include <stddef.h>

int main(void) {
    size_t n = 10;
    int    i = 3;

    size_t        a   = n * i;                 /* ulong * int          -> 30  */
    size_t        b   = n / sizeof(int);       /* ulong / int(sizeof)  -> 2   */
    unsigned long c   = (0xF0UL) & i;          /* ulong & int          -> 0   */
    int           cmp = (n > i);               /* ulong > int compare  -> 1   */
    size_t        d   = (i < 0 ? n : n + i);   /* ternary: ulong arms  -> 13  */

    long total = (long)a + (long)b + (long)c + cmp + (long)d;  /* 46 */
    printf("%ld\n", total);
    return 0;
}
