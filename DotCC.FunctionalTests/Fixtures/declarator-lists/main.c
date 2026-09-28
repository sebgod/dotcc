/* Init-declarators (GH #223, C11 6.7.6 / 6.7.9): every declarator form in every
   position of a declarator list, at every scope. CPython declares arrays next
   to pointers (`char rawmode[6], *m;`), sizes the outer bound of a 2-D table
   from its initializer (`specs[][2]`), parenthesizes an array declarator
   (`PyObject *(values[N])`), and mixes fn-ptr tables into lists. */
#include <stdio.h>
#include <string.h>

static int twice(int x) { return 2 * x; }
static int thrice(int x) { return 3 * x; }

/* File scope: a scalar, an array and a pointer in one list, a table sized by
   its initializer, a fn-ptr table beside a plain fn-ptr, a braced scalar. */
int counter = 3, table[4] = { 1, 2, 3, 4 }, *cursor;
static const char *names[] = { "zero", "one", "two" }, *fallback = "none";
int (*ops[])(int) = { twice, thrice }, (*chosen)(int) = thrice;
static int braced = { 42 };
const char specs[][2] = { { 'a', 'b' }, { 'c', 'd' }, { 'e', 'f' } };

struct mode { char rawmode[6], *m; int flags[2], n; };

static int bump(void)
{
    /* A static local list mixing a counter and a lookup table. */
    static int calls = 0, seen[3];
    seen[calls % 3] += 1;
    return ++calls * 10 + seen[0];
}

int main(void)
{
    char rawmode[6], *m;
    strcpy(rawmode, "rb+");
    m = rawmode + 1;
    printf("%s %s %d\n", rawmode, m, (int)sizeof rawmode);

    int a = 1, b[3] = { 4, 5, 6 }, *p = b + 1, c[2][2] = { { 7, 8 }, { 9, 10 } };
    printf("%d %d %d %d %d\n", a, b[2], *p, c[1][0], (int)(sizeof c / sizeof c[0]));

    char text[] = "hi", *q = text, wide[8] = "pad";
    printf("%s %c %d %d\n", q, wide[1], (int)sizeof text, wide[5]);

    const char *(values[3]) = { "x", "y", "z" };
    printf("%s%s%s %d\n", values[0], values[1], values[2], (int)(sizeof values / sizeof values[0]));

    int n = { 5 };
    struct mode md = { "wb", 0, { 1, 2 }, 9 };
    md.m = md.rawmode;
    printf("%d %s %s %d %d\n", n, md.rawmode, md.m, md.flags[1], md.n);

    printf("%d %d %d %s %s\n", counter, table[3], ops[0](7) + ops[1](1) + chosen(2), names[2], fallback);
    cursor = table + 2;
    printf("%d %d %c%c %d\n", *cursor, braced, specs[2][0], specs[1][1], (int)(sizeof specs / sizeof specs[0]));

    int r1 = bump(), r2 = bump(), r3 = bump(), r4 = bump();
    printf("%d %d %d %d\n", r1, r2, r3, r4);
    return 0;
}
