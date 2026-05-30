#include <stdio.h>

/* Anonymous (tagless) typedef struct — `typedef struct { … } Name;`, the most
   common typedef-struct idiom in real C. dotcc emits the C# struct under the
   alias name (no tag to bind). Nested use (one anon typedef referencing
   another) and compound literals over the alias both work. */

typedef struct { int x; int y; } Point;
typedef struct { Point origin; int size; } Box;

int main(void) {
    Point p = {3, 4};
    Point q = (Point){.x = 10, .y = 20};   /* compound literal over the alias */
    Box b;
    b.origin = p;
    b.size = 7;

    printf("%d %d\n", p.x, p.y);
    printf("%d %d\n", q.x, q.y);
    printf("%d %d %d\n", b.origin.x, b.origin.y, b.size);
    return 0;
}
