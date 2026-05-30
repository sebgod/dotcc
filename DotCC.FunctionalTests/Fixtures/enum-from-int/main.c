/* int <-> enum flow: C lets an int initialize/assign an enum and vice versa.
 * dotcc inserts the casts C# requires — `enum Color c = 1` → `(Color)1`,
 * `c = c + 1` → `c = (Color)((int)c + 1)`, and an enum-returning function whose
 * `return c + 1` (int) casts back to the enum. A call to an enum-returning
 * function is itself enum-typed (so the result needs no redundant cast). */
#include <stdio.h>

enum Color { Red, Green, Blue };

enum Color next(enum Color c) {
    return c + 1;              /* enum+int -> int, cast to Color on return */
}

int main(void) {
    enum Color c = 1;          /* int -> enum: Green */
    c = c + 1;                 /* Blue (2) */
    enum Color d = next(Red);  /* next(0) -> Green (1) */
    int n = next(d);           /* enum result decays to int: next(1)=2 */
    printf("%d %d %d\n", c, d, n);   /* 2 1 2 */
    return 0;
}
