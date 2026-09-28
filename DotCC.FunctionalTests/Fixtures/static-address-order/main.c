/* Static storage exists before any initializer runs (C11 5.1.2p1, 6.2.4p3), so an
   address constant may name an object defined later in the file, in another file, or
   the object it initializes (C11 6.2.1p7). */
#include <stdio.h>

struct node { struct node *next; int v; };

/* An array whose elements point into itself, as CPython's parking_lot buckets do. */
static struct node ring[3] = { { &ring[1], 1 }, { &ring[2], 2 }, { &ring[0], 3 } };

/* Defined in table.c, which comes after this file. */
extern int table[];
extern const char *names[];
int *first = table;
const char **name0 = &names[0];

/* An array of pointers to arrays defined in table.c. */
int *rows[2] = { table, table + 2 };

/* A pointer to an object with a flexible array member, defined further down. */
struct counted { int n; int items[]; };
extern struct counted bag;
struct counted *bagp = &bag;
struct counted bag = { 3, { 7, 8, 9 } };

int main(void) {
    struct node *p = &ring[0];
    for (int i = 0; i < 6; i++) { printf("%d", p->v); p = p->next; }
    printf("\n%d %d %d\n", *first, rows[0][1], rows[1][0]);
    printf("%s %s\n", *name0, names[1]);
    printf("%d %d %d\n", bagp->n, bagp->items[0], bagp->items[2]);
    return 0;
}
