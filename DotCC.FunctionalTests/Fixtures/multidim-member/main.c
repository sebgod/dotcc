#include <stdio.h>

/* Phase 4l — MULTI-DIMENSIONAL struct array members. `T grid[N][M];` flattens to
 * one buffer (a `fixed T[N*M]` for a primitive element, `[InlineArray(N*M)]` for a
 * non-primitive one) and `s.grid[i][j]` rewrites to flat pointer striding — the
 * same machinery block-scope multi-dim arrays use. This is Lua's
 * `TString *strcache[STRCACHE_N][STRCACHE_M];` shape. */

#define ROWS 2
#define COLS 3

struct Grid {
    int   cells[ROWS][COLS];   /* primitive → fixed int[6] */
    char *names[2][2];         /* pointer → [InlineArray(4)] */
};

int main(void) {
    struct Grid g;
    for (int i = 0; i < ROWS; i++)
        for (int j = 0; j < COLS; j++)
            g.cells[i][j] = i * 10 + j;
    g.names[0][0] = "a";
    g.names[1][1] = "d";
    printf("%d %d %d\n", g.cells[0][0], g.cells[1][2], g.cells[0][2]);
    printf("%s %s\n", g.names[0][0], g.names[1][1]);
    return 0;
}
