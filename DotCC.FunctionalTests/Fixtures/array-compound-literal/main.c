#include <stdio.h>

/* C99 array compound literal — `(int[]){…}` / `(int[N]){…}`. dotcc lowers it to
   a `stackalloc T[]{…}`, valid in initializer position (a stackalloc can't
   escape to a pointer in other positions in C#). Sized forms zero-fill to the
   declared length; implicit `[]` takes the initializer's length. */

int main(void) {
    int *p = (int[]){10, 20, 30};       /* implicit size 3 */
    int *q = (int[5]){1, 2};            /* sized, zero-fill to 5 */
    double *d = (double[]){1.5, 2.5};

    printf("%d %d %d\n", p[0], p[1], p[2]);
    printf("%d %d %d %d %d\n", q[0], q[1], q[2], q[3], q[4]);
    printf("%.1f %.1f\n", d[0], d[1]);
    return 0;
}
