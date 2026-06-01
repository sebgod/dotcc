#include <stdio.h>

/* dotcc has no C# notion of const/volatile, so it drops both qualifiers at the
 * token level (QualifierStripper). This fixture exercises every position a
 * qualifier can appear — especially the ones the grammar could not parse before
 * the stripper: a qualifier in front of a typedef-name or a struct tag. */

typedef struct Point { int x; int y; } Point;

/* west const on a typedef-name parameter (was a parse error: `const TYPE_NAME`) */
static int sumx(const Point *a, const Point *b) {
    return a->x + b->x;
}

/* east const + const pointer-to-const */
static int firsty(Point const * const p) {
    return p->y;
}

/* const in front of a struct tag (the tag is not a typedef-name here) */
struct Box { int w; int h; };
static int area(const struct Box *bx) {
    return bx->w * bx->h;
}

typedef int Int;

int main(void) {
    const volatile Point p = { 3, 4 };   /* const volatile on an aggregate */
    Point q = { 10, 20 };
    const Int k = 5;                      /* const on a typedef-name local */
    struct Box b = { 6, 7 };
    int s = sumx((const Point *)&p, &q);  /* const in a cast */
    printf("sumx=%d\n", s);
    printf("firsty=%d\n", firsty(&q));
    printf("area=%d\n", area(&b));
    printf("k=%d\n", k);
    return 0;
}
