#include <stdio.h>

/* Named nested aggregate members: `struct { … } name;` / `union { … } name;`.
 * Unlike anonymous (C11) nested members — whose fields promote into the parent
 * (`o.x`) — a NAMED one is a real field of an unnamed type, accessed as
 * `o.name.inner`. dotcc synthesizes a nested C# type for it. Lua's
 * `union StackValue { TValue val; struct { …; unsigned short delta; } tbc; }`
 * is the motivating shape. */

struct Outer {
    int tag;
    struct { int x; int y; } pt;     /* named nested struct */
    union { int i; float f; } u;     /* named nested union (overlapping) */
};

typedef union StackValue {
    long val;
    struct { int lo; unsigned short delta; } tbc;   /* named nested struct in a union */
} StackValue;

int main(void) {
    struct Outer o;
    o.tag = 1; o.pt.x = 3; o.pt.y = 4; o.u.f = 2.5f;
    StackValue sv;
    sv.tbc.lo = 7; sv.tbc.delta = 9;
    printf("tag=%d pt=%d,%d u=%g tbc=%d,%d\n",
           o.tag, o.pt.x, o.pt.y, o.u.f, sv.tbc.lo, (int)sv.tbc.delta);
    return 0;
}
