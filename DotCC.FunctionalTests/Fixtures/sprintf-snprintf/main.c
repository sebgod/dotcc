// sprintf / snprintf lowered to the fluent SprintfBuilder (.Arg(...).Done()).
// Exercises the arg-overload surface (int, string, long) and the C99 snprintf
// truncation + would-be-length return. Oracled against gcc/MSVC.

#include "stdio.h"

int main() {
    char buf[64];

    // sprintf: format into a buffer, then print it.
    sprintf(buf, "%d-%s", 42, "hi");
    puts(buf);                                  // 42-hi

    // sprintf returns the byte count written (excl. NUL).
    int n = sprintf(buf, "abc%d", 99);
    printf("wrote %d: %s\n", n, buf);           // wrote 5: abc99

    // A long argument must route through Arg(long), not silently to a float.
    sprintf(buf, "%ld", 1234567890L);
    puts(buf);                                  // 1234567890

    // snprintf truncates to n-1 chars + NUL, and returns the would-be length.
    int k = snprintf(buf, 4, "hello");
    printf("k=%d buf=%s\n", k, buf);            // k=5 buf=hel

    // snprintf with a roomy bound writes the whole thing.
    snprintf(buf, 64, "x=%d", 7);
    puts(buf);                                  // x=7

    return 0;
}
