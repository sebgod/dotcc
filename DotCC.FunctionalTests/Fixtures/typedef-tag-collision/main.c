#include <stdio.h>

/* The canonical `typedef struct Foo Foo;` idiom: a struct/union/enum TAG and a
 * typedef-name that deliberately share a spelling. C keeps tags in their own
 * namespace, so this is legal — but the typedef-name lexer hack must NOT promote
 * the identifier right after `struct`/`union`/`enum` to TYPE_NAME, or the
 * definition (and every `struct Foo` reference) feeds a TYPE_NAME where the
 * grammar wants a plain tag ID. This is pervasive in real C (Lua's lua_State,
 * Table, TString, …). */

typedef struct Point Point;
struct Point { int x; int y; };

typedef union Box Box;
union Box { int i; float f; };

typedef enum Color Color;
enum Color { RED, GREEN, BLUE };

/* `const struct Point *` — a tag reference carrying a qualifier (Phase 4a). */
static int manhattan(const struct Point *p) { return p->x + p->y; }

static Color next_color(Color c) { return (Color)((c + 1) % 3); }

int main(void) {
    Point p = { 3, 4 };
    Box b; b.i = 42;
    Color c = GREEN;
    printf("m=%d box=%d color=%d next=%d\n",
           manhattan(&p), b.i, (int)c, (int)next_color(c));
    return 0;
}
