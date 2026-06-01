#include <stdio.h>

/* Phase 4k — struct array-member bounds that are integer CONSTANT EXPRESSIONS
 * (not just literals): a sizeof, an enum constant, and arithmetic over them — each
 * folded to the literal a C# `fixed[N]` needs. Also: a typedef element (`byte_t` →
 * `unsigned char` → `byte`) resolves to the primitive `fixed byte` buffer. The
 * out-of-low-index accesses below are in-bounds only if each size folded right. */

typedef unsigned char byte_t;          /* resolves to byte for the fixed-buffer check */
enum Sizes { BASE = 3, SLOTS };        /* SLOTS auto-numbers to 4 */

struct Buffers {
    byte_t raw[sizeof(void *)];        /* sizeof bound → 8 */
    int    slots[SLOTS];               /* enum-constant bound → 4 */
    long   scaled[2 * BASE];           /* arithmetic bound → 6 */
};

int main(void) {
    struct Buffers b;
    b.raw[0] = 1; b.raw[7] = 2;        /* index 7 valid iff size == 8 */
    b.slots[3] = 30;                   /* index 3 valid iff size == 4 */
    b.scaled[5] = 500;                 /* index 5 valid iff size == 6 */
    printf("%d %d %d %d\n", (int)b.raw[0], (int)b.raw[7], b.slots[3], (int)b.scaled[5]);
    return 0;
}
