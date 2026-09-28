/* Anonymous struct and union definitions in two headers, at the same lines and
 * columns. Each is its own type: dotcc once cached anonymous aggregates by
 * source position, which carries no file, so b.h's members took a.h's types
 * (CPython's pycore_tracemalloc.h `allocators` became cpython/pystate.h's
 * bit-field struct). */
#include <stdio.h>
#include "a.h"
#include "b.h"

int main(void) {
    struct first f = { { 3, 4 }, { 65 } };
    struct later g = { { 2.5, { 'o', 'k', 0 } }, { 0 } };
    g.u.s[3] = 7;

    printf("%d %d %d\n", f.pair.x, f.pair.y, f.u.i);
    printf("%g %s %d\n", g.pair.d, g.pair.tag, g.u.s[3]);
    printf("%zu %zu %zu %zu\n", sizeof f.pair, sizeof f.u, sizeof g.pair, sizeof g.u);
    return 0;
}
