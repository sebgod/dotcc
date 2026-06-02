/* `static` struct/union AGGREGATE initializers — file-scope and block-scope,
 * including a NESTED brace for a union member. Lua's lcode.c has a block-scope
 * `static const expdesc ef = {VKINT, {0}, NO_JUMP, NO_JUMP}` where the `{0}`
 * initializes the union member `u`. dotcc lowers a `static T x = {...}` to a
 * once-initialised DotCcGlobals field, recursing the nested brace into the
 * field's type (`u = new <UnionType> { i = 42 }` — the union's first member).
 * All values are ABI-stable (int). gcc/MSVC agree. */
#include <stdio.h>

typedef struct Point { int x; int y; } Point;

/* file-scope static struct with a positional aggregate initializer */
static const Point origin = {10, 20};

typedef struct Tagged {
    int kind;
    union { int i; float f; } u;     /* a union member, nested-brace-initialized */
    int extra;
} Tagged;

static int reader(void) {
    /* block-scope static: persists across calls, initialised once. The `{42}`
       initializes the union's first member (i). */
    static const Tagged t = {7, {42}, -1};
    return t.kind + t.u.i + t.extra;     /* 7 + 42 + (-1) = 48 */
}

int main(void) {
    printf("%d %d\n", origin.x, origin.y);   /* 10 20 */
    printf("%d\n", reader());                /* 48 */
    printf("%d\n", reader());                /* 48 (static persists) */
    return 0;
}
