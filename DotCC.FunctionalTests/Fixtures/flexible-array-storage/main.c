/* GH #246: a flexible array member (C99) and a GNU zero-length array member
 * have real layout: no storage of their own, an offset after the members
 * before them (aligned to the element), and a struct size as if they were
 * omitted, plus trailing padding to their alignment. A statically initialized
 * object with a flexible array member gets storage for its initializer (a GNU
 * extension), as CPython's shared empty-keys dictionary does (dictobject.c),
 * and sre's TemplateObject reads a zero-length `items[0]` (sre.h). */
#include <stdio.h>
#include <stdlib.h>
#include <stddef.h>

struct keys { long size; char kind; signed char indices[]; };   /* after a padded tail */
struct wide { char tag; int vals[]; };                          /* more aligned than the rest */
struct tmpl { int count; struct { int index; void *literal; } items[0]; };

static struct keys empty_keys = { 8, 'e', { -1, -1, -1, -1, -1, -1, -1, -1 } };

int main(void) {
    printf("%zu %zu %zu %zu\n", sizeof(struct keys), offsetof(struct keys, indices),
           sizeof(struct wide), offsetof(struct wide, vals));
    printf("%zu %zu\n", sizeof(struct tmpl), offsetof(struct tmpl, items));

    int sum = 0;
    for (int i = 0; i < empty_keys.size; i++) sum += empty_keys.indices[i];
    printf("%ld %c %d\n", empty_keys.size, empty_keys.kind, sum);
    struct keys *k = &empty_keys;
    k->indices[3] = 7;
    printf("%d %d\n", empty_keys.indices[3], (&empty_keys)->indices[7]);

    struct wide *w = malloc(offsetof(struct wide, vals) + 3 * sizeof(int));
    w->tag = 'w';
    for (int i = 0; i < 3; i++) w->vals[i] = (i + 1) * 100;
    int *first = w->vals;
    printf("%c %d %d %d\n", w->tag, first[0], w->vals[1], w->vals[2]);
    free(w);

    struct tmpl *t = malloc(sizeof(struct tmpl) + 2 * sizeof(t->items[0]));
    int x = 5, y = 6;
    t->count = 2;
    t->items[0].index = 10;
    t->items[0].literal = &x;
    t->items[1].index = 20;
    t->items[1].literal = &y;
    printf("%d %d %d %d\n", t->count, t->items[1].index,
           *(int *)t->items[0].literal, *(int *)t->items[1].literal);
    free(t);
    return 0;
}
