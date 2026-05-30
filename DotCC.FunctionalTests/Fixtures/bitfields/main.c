/* Bit-fields. C# has no bit-fields, so dotcc lowers each named bit-field to a
 * FULL field of its declared type (the width is dropped) and an anonymous
 * bit-field (padding) to nothing. This is correct for values that FIT the
 * declared width — the common case for flags/small fields. It does NOT
 * truncate/wrap on overflow and the struct size differs from C (documented
 * limitation), so this fixture only stores in-range values. gcc is the oracle. */
#include <stdio.h>

struct Flags {
    unsigned ready : 1;
    unsigned       : 2;   /* anonymous padding — no accessible member */
    unsigned mode  : 3;
    unsigned count : 4;
};

int main(void) {
    struct Flags f;
    f.ready = 1;
    f.mode  = 5;          /* fits in 3 bits (max 7) */
    f.count = 10;         /* fits in 4 bits (max 15) */
    printf("ready=%u mode=%u count=%u\n", f.ready, f.mode, f.count);
    return 0;
}
