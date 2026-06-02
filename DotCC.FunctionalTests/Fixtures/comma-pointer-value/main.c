/* A value-context comma whose VALUE (or a leading operand) is a POINTER — Lua's
 * `check_exp(c, e)` = `(lua_assert(c), e)` with a pointer `e`, nested in value
 * position (inside a cast / member access / `&`), so it can't be statement-
 * hoisted like the void-comma case. A pointer can't be a C# `ValueTuple` element
 * (CS0306), so dotcc casts pointer operands to `nint` for the tuple (pointer-width,
 * round-trips) and casts the `.ItemN` back to the pointer type when the comma's
 * value is a pointer. */
#include <stdio.h>
#include <stddef.h>

struct Node { int v; struct Node *next; };

int main(void) {
    struct Node a, b;
    a.v = 10; a.next = &b;
    b.v = 20; b.next = NULL;
    struct Node *p = &a;

    /* (discard, ptr) -> member, nested deep in value position */
    int x = ((void)(0), p)->v;                  /* 10 */
    /* (discard, ptr) as an assigned pointer value */
    struct Node *q = ((void)(0), p->next);      /* &b */
    /* 3-operand comma: a side effect, a discard, then a pointer value */
    struct Node *r = (p->v += 1, (void)0, p);   /* a.v: 10 -> 11; value = p */
    printf("%d %d %d\n", x, q->v, r->v);         /* 10 20 11 */
    return 0;
}
