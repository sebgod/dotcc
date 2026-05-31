#include <stdio.h>

/* C99 array compound literal — `(int[]){…}` / `(int[N]){…}`, plus struct-element
   and designated forms. dotcc lowers each to a `stackalloc T[]{…}`, valid in
   initializer position (a stackalloc can't escape to a pointer in other
   positions in C#). Sized forms zero-fill to the declared length; implicit `[]`
   takes the initializer's length. */

struct P { int x; int y; };

int main(void) {
    int *p = (int[]){10, 20, 30};         /* implicit size 3 */
    int *q = (int[5]){1, 2};              /* sized, zero-fill to 5 */
    double *d = (double[]){1.5, 2.5};
    struct P *pts = (struct P[]){ {1, 2}, {3, 4} };   /* struct elements */
    int *g = (int[]){ [2] = 9, [4] = 1 };             /* designated → 0 0 9 0 1 */

    printf("%d %d %d\n", p[0], p[1], p[2]);
    printf("%d %d %d %d %d\n", q[0], q[1], q[2], q[3], q[4]);
    printf("%.1f %.1f\n", d[0], d[1]);
    printf("%d %d %d %d\n", pts[0].x, pts[0].y, pts[1].x, pts[1].y);
    printf("%d %d %d %d %d\n", g[0], g[1], g[2], g[3], g[4]);
    return 0;
}
