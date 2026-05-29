/* malloc → stack-value peephole. `p` is allocated with malloc(sizeof(S)),
 * used ONLY through `->`, and freed in the same function with no escape (not
 * returned, not passed anywhere, not address-taken). dotcc lowers it to a
 * stack struct value (`Point p = new Point();`, `p.x`, dropped free) — no
 * native heap allocation. Observable behaviour is identical to the malloc
 * form, which is what the gcc oracle confirms. */
#include <stdio.h>
#include <stdlib.h>

struct Point {
    int x;
    int y;
};

int main(void) {
    struct Point* p = (struct Point*)malloc(sizeof(struct Point));
    p->x = 3;
    p->y = 4;
    printf("%d\n", p->x + p->y);
    free(p);
    return 0;
}
