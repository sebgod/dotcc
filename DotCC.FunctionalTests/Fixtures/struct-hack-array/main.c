#include <stdio.h>
#include <stdlib.h>

/* The "struct hack": a trailing array member sized [1] in the declaration but
 * over-allocated at malloc time and indexed past its nominal length, into the
 * tail. For a PRIMITIVE element dotcc uses a C# `fixed` buffer (no bounds check,
 * decays to a pointer — both for free). For a NON-PRIMITIVE element (struct /
 * pointer), `fixed` is illegal, so dotcc lowers to a C# 12 [InlineArray] and
 * routes access through the element pointer (`(T*)&field`), restoring the
 * over-indexing + decay that `fixed` gave. This is Lua's Udata / Closure shape. */

typedef struct Pt { int x; int y; } Pt;

typedef struct Poly {
    int n;
    Pt pts[1];                 /* struct-hack trailing array of STRUCT elements */
} Poly;

typedef struct Bag {
    int n;
    Pt *items[1];              /* struct-hack trailing array of POINTER elements */
} Bag;

static int sum_x(Pt *p, int n) {          /* receives a decayed Pt* */
    int s = 0;
    for (int i = 0; i < n; i++) { s += p[i].x; }
    return s;
}

int main(void) {
    int n = 3;
    Poly *poly = (Poly *)malloc(sizeof(Poly) + (n - 1) * sizeof(Pt));
    poly->n = n;
    for (int i = 0; i < n; i++) { poly->pts[i].x = i + 1; poly->pts[i].y = 0; }  /* over-index write */

    Pt a = { 10, 0 };
    Pt b = { 20, 0 };
    Bag *bag = (Bag *)malloc(sizeof(Bag) + 1 * sizeof(Pt *));
    bag->n = 2;
    bag->items[0] = &a;
    bag->items[1] = &b;        /* over-index write of a pointer element */

    printf("sub=%d decay=%d ptr=%d sz=%d\n",
           poly->pts[2].x,                 /* over-index read  → 3 */
           sum_x(poly->pts, n),            /* array→pointer decay → 1+2+3 = 6 */
           bag->items[1]->x,               /* over-index read of pointer → 20 */
           (int)sizeof(poly->pts));        /* count(1) * sizeof(Pt)(8) = 8 */
    free(poly);
    free(bag);
    return 0;
}
