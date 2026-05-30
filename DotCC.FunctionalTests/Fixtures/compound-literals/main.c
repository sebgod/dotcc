#include <stdio.h>

/* C99 compound literals: (Type){ init } — an unnamed object usable in any
   expression position. dotcc lowers struct/union compound literals to a C#
   object-creation `new Type { field = val, … }`. Positional + designated
   forms, in initializer / assignment / call-argument / return positions. */

struct Point { int x; int y; };
typedef struct Color { int r; int g; int b; } Color;

int sumpt(struct Point p) { return p.x + p.y; }

struct Point origin(void) { return (struct Point){0, 0}; }  /* return position */

int main(void) {
    struct Point p = (struct Point){3, 4};                  /* positional init */
    Color c = (Color){.r = 255, .g = 128, .b = 0};          /* designated init */
    struct Point q;
    q = (struct Point){7, 8};                               /* assignment */
    struct Point o = origin();

    printf("%d %d\n", p.x, p.y);
    printf("%d\n", c.r + c.g + c.b);
    printf("%d %d\n", q.x, q.y);
    printf("%d\n", sumpt((struct Point){10, 20}));           /* call argument */
    printf("%d %d\n", o.x, o.y);
    return 0;
}
