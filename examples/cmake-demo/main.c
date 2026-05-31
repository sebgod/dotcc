#include <stdio.h>
#include "geom.h"

int main(void) {
    struct Point a = {1, 2};
    struct Point b = {4, 6};

    /* manhattan = |1-4| + |2-6| = 3 + 4 = 7 ; dot = 1*4 + 2*6 = 16 */
    printf("manhattan=%d dot=%d\n", manhattan(a, b), dot(a, b));
    return 0;
}
