#include <stdio.h>
#include <stddef.h>

/* offsetof(Type, member) — Lua's flexible-array malloc-sizing idiom. A regular
 * field and a fixed-buffer array member. All-int so the layout is identical across
 * compilers (no alignment ambiguity); offsetof of the first member is 0. */

struct S { int a; int grid[2]; int b; };

int main(void) {
    printf("%zu %zu %zu\n",
        offsetof(struct S, a),
        offsetof(struct S, grid),
        offsetof(struct S, b));
    return 0;
}
