// Union: classic int / float type-pun. Reading a different member from the
// one written gives the raw bytes through the other type's interpretation.

#include "stdio.h"

union IntFloat {
    int i;
    float f;
};

int main() {
    union IntFloat u;
    u.i = 0x40490FDB;  // IEEE-754 single precision bits for ~3.14159
    printf("i=0x%X\n", u.i);
    // Read as float — gets the same bytes interpreted differently.
    printf("f=%.5f\n", u.f);

    // Write through `f`, read back through `i`.
    u.f = 1.0f;        // IEEE-754 bits 0x3F800000
    printf("f=%.1f\n", u.f);
    printf("i=0x%X\n", u.i);

    return 0;
}
