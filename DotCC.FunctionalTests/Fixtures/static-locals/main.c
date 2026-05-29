/* Static storage duration: file-scope `static` (internal linkage) and
 * block-scope function statics (persist across calls, function-local). Two
 * functions declare a same-named `static int counter` to prove the lowering
 * mangles per-function (no collision). The side-effecting calls are sequenced
 * into locals before printf — C doesn't fix function-argument evaluation order,
 * so this keeps the output deterministic across compilers. */
#include <stdio.h>

static int total = 0;   /* file-scope static */

int next_id(void) {
    static int counter = 0;   /* persists across calls */
    counter++;
    total += counter;
    return counter;
}

int ticks(void) {
    static int counter = 100; /* same name, different function */
    counter += 10;
    return counter;
}

int main(void) {
    int a = next_id();
    int b = ticks();
    printf("%d %d\n", a, b);

    int c = next_id();
    int d = ticks();
    printf("%d %d\n", c, d);

    printf("%d\n", total);
    return 0;
}
