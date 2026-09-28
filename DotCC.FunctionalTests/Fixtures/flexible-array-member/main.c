#include <stdio.h>
#include <stdlib.h>

/* C99 flexible array member (sized at malloc time) + C89 sized array member.
   The sized one lowers to a C# fixed-size buffer; the flexible one has no
   storage of its own and is its offset in the malloc'd object (GH #246), so
   sizeof(struct Vec) is C's 4 and the malloc(sizeof(S)+n*sizeof(T)) idiom
   allocates exactly what C does. */

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
