#include <stdio.h>

/* Multi-declarator lists with per-declarator pointers — `int *a, *b;` (both
 * pointers), `int *c, d;` (c pointer, d not). C binds `*` to each declarator;
 * dotcc's Type greedily takes the first declarator's `*`s, so a subsequent
 * declarator carries its own (DeclItemTail). A uniform list stays one C#
 * multi-declarator; a mixed one splits into separate declarations (C# binds `*`
 * to the type). Also covers multi-declarator STRUCT members (Lua's
 * `struct CallInfo *previous, *next;`). */

typedef struct Link { int v; struct Link *prev, *next; } Link;   /* multi-decl member + per-decl ptrs */
struct Mixed { int a, b; int *p, q; };                           /* a,b int; p int*; q int */

int main(void) {
    int x = 1, y = 2;          /* plain multi-declarator */
    int *a = &x, *b = &y;      /* uniform: both int* */
    int *c = &x, d = 99;       /* mixed: c is int*, d is int */

    Link n3; n3.v = 3; n3.prev = &n3; n3.next = &n3;
    Link n2; n2.v = 2; n2.prev = &n3; n2.next = &n3;

    struct Mixed m;
    m.a = 10; m.b = 20; m.p = &m.a; m.q = 30;

    printf("xy=%d ab=%d cd=%d link=%d mixed=%d,%d,%d\n",
           x + y, *a + *b, *c + d, n2.next->v, m.a + m.b, *m.p, m.q);
    return 0;
}
