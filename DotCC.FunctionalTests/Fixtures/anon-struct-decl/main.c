/* An ANONYMOUS struct type used directly in a declaration (not a typedef body
 * or a struct member) — Lua's lparser.c priority table:
 *   static const struct { lu_byte left; lu_byte right; } priority[] = { … };
 * dotcc synthesizes a name for the unnamed struct, emits its decl, and lets the
 * ordinary array / aggregate-init machinery handle the rest (each element is a
 * nested brace `{10,10}` → `new <synth> { left=10, right=10 }`). The same works
 * for a block-scope anon-struct variable and the sizeof array-length idiom.
 * All values ABI-stable (unsigned char / int). gcc/MSVC agree. */
#include <stdio.h>

typedef unsigned char lu_byte;

static const struct { lu_byte left; lu_byte right; } priority[] = {
    {10, 10}, {11, 11}, {14, 13}, {3, 3}, {2, 1}
};

int main(void) {
    int n = (int)(sizeof(priority) / sizeof(priority[0]));   /* 5 */
    printf("%d\n", n);
    printf("%d %d\n", priority[0].left, priority[0].right);  /* 10 10 */
    printf("%d %d\n", priority[2].left, priority[2].right);  /* 14 13 */
    printf("%d %d\n", priority[4].left, priority[4].right);  /* 2 1 */

    /* a block-scope anonymous-struct variable */
    struct { int a; int b; } pt = {5, 6};
    printf("%d\n", pt.a + pt.b);                             /* 11 */
    return 0;
}
