#include <stdio.h>
#include "geom.h"
#include "buildcfg.h"   /* lives in include/ — reachable only via -I */

int main(void) {
    struct Point a = {1, 2};
    struct Point b = {4, 6};

    /* manhattan = |1-4| + |2-6| = 3 + 4 = 7 ; dot = 1*4 + 2*6 = 16.
       BUILD_TAG comes from buildcfg.h (found via -I include); SCALE comes
       from -DSCALE=10 (target_compile_definitions) and is used numerically,
       so scaled = 7*10 = 70 proves the value — not just the name — threaded. */
    int m = manhattan(a, b);
    printf("manhattan=%d dot=%d tag=%s scaled=%d\n", m, dot(a, b), BUILD_TAG, m * SCALE);
    return 0;
}
