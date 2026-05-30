/* Multi-dimensional arrays. C# stackalloc is 1-D, so dotcc flattens `int a[2][3]`
 * to `stackalloc int[6]` (row-major, like C) and rewrites a partial subscript
 * `a[i]` to `a + i*stride` and a full `a[i][j]` to the flat element. The nested
 * sizeof length idiom (rows = sizeof(a)/sizeof(a[0]), cols = sizeof(a[0])/
 * sizeof(a[0][0])) falls out of the tracked shape. gcc is the oracle. */
#include <stdio.h>

int main(void) {
    int a[2][3];
    for (int i = 0; i < 2; i++)
        for (int j = 0; j < 3; j++)
            a[i][j] = i * 3 + j;

    int sum = 0;
    for (int i = 0; i < 2; i++)
        for (int j = 0; j < 3; j++)
            sum += a[i][j];

    printf("a[1][2]=%d sum=%d rows=%d cols=%d\n",
           a[1][2], sum,
           (int)(sizeof(a) / sizeof(a[0])),
           (int)(sizeof(a[0]) / sizeof(a[0][0])));
    return 0;
}
