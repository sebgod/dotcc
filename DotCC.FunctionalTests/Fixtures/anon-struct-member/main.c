#include <stdio.h>

/* C11 anonymous struct member — `struct { … };` with no name, whose fields are
   promoted into the enclosing aggregate. For a struct (sequential, no overlap)
   that's an inline: dotcc emits the inner fields directly into the parent, so
   `o.x` resolves to a real field and the layout matches C. */

struct Outer {
    int tag;
    struct { int x; int y; };   /* x, y promoted into Outer */
    int z;
};

int main(void) {
    struct Outer o;
    o.tag = 1;
    o.x = 10;
    o.y = 20;
    o.z = 30;
    printf("%d %d %d %d\n", o.tag, o.x, o.y, o.z);
    return 0;
}
