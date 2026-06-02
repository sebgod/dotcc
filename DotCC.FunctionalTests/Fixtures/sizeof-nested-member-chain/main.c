/* sizeof through a member chain that passes through an ANONYMOUS inline struct
 * member. Lua's ldo.c does `sizeof(*p.dyd.actvar.arr)` where `actvar` is a
 * `struct { Vardesc *arr; int n; int size; }` member of Dyndata — an unnamed
 * inline struct lowered to a synthesized nested type. dotcc now records that
 * synth type's field types (and the parent field's type), so the chain
 * `o.vec.arr` carries its CType and `sizeof(*o.vec.arr)` / `sizeof(o.vec.arr[i])`
 * resolve to the element size. All sizes here are ABI-stable (int=4). gcc agrees. */
#include <stdio.h>

typedef struct { int a; int b; } Elem;    /* sizeof == 8 everywhere */

typedef struct Outer {
    struct {                               /* anonymous inline struct member */
        Elem *arr;
        int n;
    } vec;
    int tag;
} Outer;

int main(void) {
    Elem buf[2];
    buf[0].a = 1; buf[0].b = 2;
    buf[1].a = 3; buf[1].b = 4;

    Outer o;
    o.vec.arr = buf;
    o.vec.n = 2;
    o.tag = 7;

    /* sizeof of a deref / subscript through the 3-level chain → sizeof(Elem). */
    printf("%d\n", (int)sizeof(*o.vec.arr));      /* 8 */
    printf("%d\n", (int)sizeof(o.vec.arr[0]));    /* 8 */

    /* the same chain resolves for ordinary access too. */
    printf("%d %d %d\n", o.vec.arr[0].a, o.vec.arr[1].b, o.tag);  /* 1 4 7 */
    return 0;
}
