// Exercises struct declaration, member access (. and ->), sizeof, and the
// heap-via-malloc pattern. Also touches struct-by-value (pass-by-value
// semantics — the callee's local copy doesn't mutate the caller's struct).

#include "stdio.h"
#include "stdlib.h"

struct Point {
    int x;
    int y;
};

// Pass-by-value: callee's `p` is a copy, caller's value is unchanged.
void try_mutate(struct Point p) {
    p.x = 999;
    p.y = 999;
}

// Pointer access: caller and callee share the same heap object.
void translate(struct Point* p, int dx, int dy) {
    p->x = p->x + dx;
    p->y = p->y + dy;
}

int main() {
    // Stack-allocated struct, fields set directly.
    struct Point stk;
    stk.x = 1;
    stk.y = 2;
    printf("stack: (%d, %d)\n", stk.x, stk.y);

    // Value-copy semantics: mutate-attempt doesn't change us.
    try_mutate(stk);
    printf("after try_mutate: (%d, %d)\n", stk.x, stk.y);

    // Heap-allocated via malloc + sizeof.
    struct Point* hp = (struct Point*)malloc(sizeof(struct Point));
    hp->x = 10;
    hp->y = 20;
    printf("heap: (%d, %d)\n", hp->x, hp->y);

    translate(hp, 5, 7);
    printf("translated: (%d, %d)\n", hp->x, hp->y);

    free(hp);
    return 0;
}
