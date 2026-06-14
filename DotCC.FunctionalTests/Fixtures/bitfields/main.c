/* Bit-fields. dotcc packs consecutive same-size bit-fields into a shared backing
 * field (MSVC storage-unit layout) + a masked/sign-extended accessor per named
 * member, so BOTH the values AND the layout (sizeof, member offsets) match C.
 *   Value semantics: a store truncates to the field width and a signed field
 *   sign-extends on read — overflow wraps, negative signed fields read back.
 *   Layout: same-size fields share a unit until it fills; a field that won't fit,
 *   a differing size, or a zero-width member (`int : 0;`) starts a fresh unit.
 * gcc and MSVC agree on every struct here (all use same-size bit-fields, where
 * the two compilers' packing rules coincide), so both oracles validate it. */
#include <stdio.h>

struct Flags {
    unsigned ready : 1;
    unsigned       : 2;   /* anonymous padding — reserves bits 1-2, no member */
    unsigned mode  : 3;
    unsigned count : 4;
    int      delta : 4;   /* signed 4-bit; int + unsigned are same size -> same unit */
};

struct Wide { unsigned a : 20; unsigned b : 20; };             /* b won't fit -> 2 units */
struct Zero { unsigned x : 4; unsigned : 0; unsigned y : 4; }; /* `: 0` forces a new unit */

int main(void) {
    struct Flags f;
    f.ready = 1;
    f.mode  = 5;          /* fits in 3 bits (max 7) */
    f.count = 10;         /* fits in 4 bits (max 15) */
    f.delta = -3;         /* signed 4-bit */
    printf("ready=%u mode=%u count=%u delta=%d\n", f.ready, f.mode, f.count, f.delta);

    /* Overflow wraps to the field width (modular store, matching C). */
    f.ready = 2;          /* 1 bit:  2 & 1  = 0  */
    f.mode  = 13;         /* 3 bits: 13 & 7 = 5  */
    f.count = 17;         /* 4 bits: 17 & 15 = 1 */
    f.delta = 13;         /* 4 bits: 1101 reads back as -3 */
    printf("ready=%u mode=%u count=%u delta=%d\n", f.ready, f.mode, f.count, f.delta);

    /* Layout now matches C — same-size fields pack, so sizeof is the real size. */
    printf("sizeof Flags=%d Wide=%d Zero=%d\n",
           (int)sizeof(struct Flags), (int)sizeof(struct Wide), (int)sizeof(struct Zero));
    return 0;
}
