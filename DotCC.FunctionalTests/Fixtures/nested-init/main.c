/* Nested-brace aggregate initializers (C89). dotcc interprets the brace tree
 * against the target type: a scalar multi-dim array flattens to a 1-D stackalloc
 * with C's per-dimension zero-fill (handling both fully-nested {{…},{…}} and
 * fully-flat {…} forms); a struct array maps each group to `new T{…}`. gcc is
 * the oracle. */
#include <stdio.h>

struct P { int x; int y; };

int main(void) {
    int m[2][3]    = {{1,2,3},{4,5,6}};    /* fully nested      */
    int n[2][3]    = {1,2,3,4,5,6};        /* flat (elision)    */
    int part[2][3] = {{1},{4,5}};          /* partial → 0-fill  */
    struct P pts[2] = {{10,20},{30,40}};   /* struct array      */
    printf("%d %d %d %d %d\n",
           m[1][2], n[0][0], part[0][1], part[1][2], pts[1].x);
    return 0;
}
