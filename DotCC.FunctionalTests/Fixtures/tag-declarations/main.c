/* Tag definitions are type specifiers and `typedef` is a storage class (GH #223,
   C11 6.7.1, 6.7.2.1, 6.7.2.2, 6.7.8): a struct/union/enum body combines with
   storage classes, qualifiers and declarators (CPython's `static const struct X
   { ... } t[] = { ... };` in posixmodule.c and arraymodule.c), untagged enums
   declare plain constants, a typedef takes a whole declarator list, and a
   block-scope typedef (ast_opt.c, getargs.c, modsupport.c) ends with its block. */
#include <stdio.h>

static const struct constdef { const char *name; int value; } conf_table[] = {
    { "PC_LINK_MAX", 8 }, { "PC_NAME_MAX", 255 }, { "PC_PATH_MAX", 4096 },
};

enum { OP_NOP, OP_LOAD, OP_STORE, OP_COUNT };
enum level { LOW = 1, HIGH = 10 } current = HIGH;

typedef int Color, *ColorPtr, Row[3], (*Combine)(int, int);
typedef struct pair { int a, b; } pair, *pair_ptr;
typedef struct { const char *label; int width; } Field;
typedef enum { MODE_READ = 4, MODE_WRITE = 2 } Mode;
typedef double scale;

struct outer {
    struct inner { int v; } in;
    union { int i; unsigned int u; };
    struct { int x, y; } pos;
    int n;
};

static int add(int x, int y) { return x + y; }

static double shrink(double d)
{
    /* A block-scope typedef shadows the file-scope `scale` to its block's end. */
    typedef int scale;
    scale half = (scale)(d / 2);
    return half;
}

int main(void)
{
    for (int i = 0; i < (int)(sizeof conf_table / sizeof conf_table[0]); i++) {
        printf("%s=%d\n", conf_table[i].name, conf_table[i].value);
    }
    printf("%d %d %d\n", OP_LOAD, OP_COUNT, current);

    Color c = 7;
    ColorPtr cp = &c;
    Row r = { 1, 2, 3 };
    Combine f = add;
    printf("%d %d %d %d\n", *cp, r[2], f(c, r[0]), (int)(sizeof(Row) / sizeof(int)));

    pair p = { 3, 4 };
    pair_ptr pp = &p;
    Field fld = { "name", 12 };
    Mode m = MODE_READ;
    printf("%d %s %d %d\n", pp->a + pp->b, fld.label, fld.width, m | MODE_WRITE);

    struct outer o = { { 5 }, { 6 }, { 7, 8 }, 9 };
    struct inner copy = o.in;
    printf("%d %d %d %d %d\n", copy.v, o.i, o.pos.x, o.pos.y, o.n);

    struct node;
    struct node { int key; struct node *next; } second = { 2, 0 }, first = { 1, &second };
    enum { NONE, SOME } flag = SOME;
    typedef struct { int w, h; } size_t2;
    size_t2 box = { 4, 5 };
    printf("%d %d %d %d\n", first.next->key, flag, box.w * box.h, (int)sizeof(struct node) > 0);

    scale s = 2.5;
    printf("%.1f %.1f\n", s, shrink(9.0));
    return 0;
}
