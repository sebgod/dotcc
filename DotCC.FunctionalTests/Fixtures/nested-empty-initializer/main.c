#include <stdio.h>

/* C23 nested empty initializers: an element of a brace-enclosed list may be the
   empty `{}`, positional or designated, and it zero-initializes that subobject
   (a struct, an array row or a scalar), at block and at file scope. */

struct Point { int x; int y; };
struct Line { struct Point a, b; int n; };

struct Line g = { .b = {}, .n = 7 };

int main(void) {
    struct Line l = { {}, { 1, 2 }, {} };
    struct Line d = { .a = { 3, 4 }, .b = {}, .n = 5 };
    int m[2][2] = { {}, { 6, 7 } };
    struct Point ps[2] = { { 8, 9 }, {} };
    printf("%d %d %d %d %d\n", l.a.x, l.a.y, l.b.x, l.b.y, l.n);
    printf("%d %d %d %d %d\n", d.a.x, d.a.y, d.b.x, d.b.y, d.n);
    printf("%d %d %d %d\n", m[0][0], m[0][1], m[1][0], m[1][1]);
    printf("%d %d %d %d\n", ps[0].x, ps[0].y, ps[1].x, ps[1].y);
    printf("%d %d %d\n", g.a.x, g.b.y, g.n);
    return 0;
}
