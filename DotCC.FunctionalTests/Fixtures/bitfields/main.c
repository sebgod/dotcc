/* Bit-fields. dotcc lowers each named bit-field to a private backing field + a
 * masked accessor property: a store truncates to the field width, and a signed
 * field sign-extends on read — so the VALUE semantics match C exactly, incl.
 * overflow wrap and negative signed fields. (The struct's sizeof/layout still
 * differ from C: bit packing is implementation-defined, so it can't match every
 * compiler. This fixture asserts only values, which gcc and MSVC agree on.) */
#include <stdio.h>

struct Flags {
    unsigned ready : 1;
    unsigned       : 2;   /* anonymous padding — no accessible member */
    unsigned mode  : 3;
    unsigned count : 4;
    int      delta : 4;   /* signed: 4-bit two's complement */
};

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
    return 0;
}
