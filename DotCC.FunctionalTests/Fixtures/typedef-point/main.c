// Exercises the C lexer hack via typedef. Three shapes:
//   1. typedef of a primitive (`typedef int Color;`)
//   2. typedef of a pointer (`typedef int* IntPtr;`)
//   3. typedef of a struct AND alias in one statement
//      (`typedef struct Point { ... } Point;`)
// Each is then used in declarations and expressions to confirm the
// TypeNameRewriter promotes ID -> TYPE_NAME and the parser routes the
// resulting Type productions correctly. The `Color * x;` form in particular
// is the textbook disambiguation case — without the hack the parser can't
// tell it from a multiplication expression.

#include "stdio.h"
#include "stdlib.h"

typedef int Color;
typedef int* IntPtr;

typedef struct Point {
    int x;
    int y;
} Point;

// Use a typedef'd type in a parameter and as a local.
Color brighten(Color c) {
    Color delta = 10;
    return c + delta;
}

// Pointer-typedef in parameter position: classic ambiguity that the lexer
// hack resolves cleanly.
int sum_through(IntPtr p) {
    return *p + *(p + 1);
}

// Struct-typedef in function signatures (no `struct` keyword needed —
// matches real C with the typedef in effect).
void translate(Point* p, int dx, int dy) {
    p->x = p->x + dx;
    p->y = p->y + dy;
}

int main() {
    Color c = 5;
    printf("brighten(%d) = %d\n", c, brighten(c));

    int arr[2];
    arr[0] = 100;
    arr[1] = 200;
    IntPtr ip = arr;
    printf("sum = %d\n", sum_through(ip));

    Point pt;
    pt.x = 1;
    pt.y = 2;
    printf("pt: (%d, %d)\n", pt.x, pt.y);

    Point* hp = (Point*)malloc(sizeof(Point));
    hp->x = 10;
    hp->y = 20;
    translate(hp, 3, 4);
    printf("translated: (%d, %d)\n", hp->x, hp->y);
    free(hp);

    return 0;
}
