/* A VOID-typed conditional expression used as a STATEMENT — Lua's GC
 * write-barrier macros, e.g. `(cond ? luaC_barrier_(...) : cast_void(0))`
 * invoked as `barrier(...);`. C# can't express such a `?:` (a void call / a
 * `(void)` cast isn't a valid ternary arm), so dotcc lowers it to an if/else
 * statement, recursing for nested void ternaries and handling it as a braceless
 * loop/if body. */
#include <stdio.h>

#define cast_void(x)        ((void)(x))
static void bump(int *p) { *p += 1; }

#define barrier(p, c)       ((c) ? bump(p) : cast_void(0))
#define barrier2(p, a, b)   ((a) ? ((b) ? bump(p) : cast_void(0)) : cast_void(0))

int main(void) {
    int n = 0;
    barrier(&n, n == 0);      /* true  -> bump -> 1 */
    barrier(&n, n > 100);     /* false -> no-op */
    barrier2(&n, 1, 1);       /* both true -> bump -> 2 */
    barrier2(&n, 1, 0);       /* inner false -> no-op */
    barrier2(&n, 0, 1);       /* outer false -> no-op */
    printf("%d\n", n);        /* 2 */

    /* as a braceless if-body and a braceless while-body */
    if (n == 2)
        barrier(&n, 1);       /* -> 3 */
    while (n < 6)
        barrier(&n, 1);       /* -> 4, 5, 6 */
    printf("%d\n", n);        /* 6 */
    return 0;
}
