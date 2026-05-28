// printf format-spec coverage: width, precision, flags, signed/space, hex.
// Each line is oracled against MSVC byte-for-byte.

#include "stdio.h"

int main() {
    // Float precision.
    printf("[%.5f]\n", 3.14159265);    // 5 decimal places
    printf("[%.1f]\n", 1.0);            // 1 decimal place
    printf("[%.0f]\n", 2.7);            // 0 decimal places
    printf("[%.10f]\n", 0.5);           // 10 decimal places
    printf("[%f]\n",   3.14);           // default 6

    // Width with right/left alignment.
    printf("[%5d]\n",  42);             // right-aligned in 5
    printf("[%-5d]\n", 42);             // left-aligned in 5
    printf("[%05d]\n", 42);             // zero-padded

    // Signed-int flags.
    printf("[%+d %+d]\n", 7, -7);       // always show sign
    printf("[% d % d]\n", 7, -7);       // space for positive

    // Width + precision on float.
    printf("[%10.3f]\n", 3.14159);
    printf("[%-10.3f]\n", 3.14159);

    // Hex with width.
    printf("[%8x]\n", 0xCAFE);
    printf("[%08X]\n", 0xCAFE);

    // String with precision (cap length).
    printf("[%.3s]\n", "hello");

    return 0;
}
