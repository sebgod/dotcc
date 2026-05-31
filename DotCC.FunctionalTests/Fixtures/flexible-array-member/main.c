#include <stdio.h>
#include <stdlib.h>

/* C99 flexible array member (sized at malloc time) + C89 sized array member.
   Both lower to C# fixed-size buffers; the FAM's [1] over-allocates by one
   element so the malloc(sizeof(S)+n*sizeof(T)) idiom stays safe. */

struct Vec { int len; int data[]; };        /* flexible array member */
struct Grid { int rows; int cells[4]; };     /* sized array member */

int main(void) {
    struct Vec *v = (struct Vec*)malloc(sizeof(struct Vec) + 3 * sizeof(int));
    v->len = 3;
    for (int i = 0; i < 3; i++) v->data[i] = (i + 1) * 10;

    struct Grid g;
    g.rows = 2;
    for (int i = 0; i < 4; i++) g.cells[i] = i * i;

    printf("%d: %d %d %d\n", v->len, v->data[0], v->data[1], v->data[2]);
    printf("%d: %d %d %d %d\n", g.rows, g.cells[0], g.cells[1], g.cells[2], g.cells[3]);
    free(v);
    return 0;
}
