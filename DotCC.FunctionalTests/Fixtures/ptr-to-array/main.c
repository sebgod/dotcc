/* Pointer-to-array `int (*p)[3]` — a pointer whose pointee is an array of 3
 * (e.g. a row pointer into a 2-D array). dotcc lowers it to a flat C# pointer
 * that subscripts with the array's stride (reusing the multi-dim machinery):
 * p[k] = p + k*3, (*p)[i] = p[i] (the pointed-to array decays to the same flat
 * pointer). sizeof(p) is the pointer size, not the array's. gcc is the oracle. */
#include <stdio.h>

int main(void) {
    int a[2][3];
    for (int i = 0; i < 2; i++)
        for (int j = 0; j < 3; j++)
            a[i][j] = i * 10 + j;

    int (*p)[3] = a;                 /* p points at row 0 of a */
    printf("%d %d %d sz=%d\n", p[1][2], (*p)[0], p[0][1], (int)sizeof(p));
    return 0;
}
