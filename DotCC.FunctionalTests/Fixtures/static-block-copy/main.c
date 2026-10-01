/* A static object's constant integer arrays are copied from read-only data in one
   block (a string's characters, a table of codes), up to the last non-zero element;
   the rest stays zero. Unused static functions and objects are left out. */
#include <stdio.h>

struct Str { long refcnt; int kind; char data[32]; };
struct Table { short codes[12]; int words[10]; unsigned char tail[16]; };
struct Outer { struct Str a; struct Str b; struct Table t; };

static struct Outer outer = {
    { 1, 2, "the quick brown fox" },
    { 3, 4, "jumps\0over" },
    { { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 0, 0 },
      { -1, 2, -3, 4, -5, 6, -7, 8 },
      { 255, 254, 0, 0, 0, 0, 0, 0, 0, 1 } },
};

static int unused_helper(int x) { return x * 3; }
static int (*unused_table[])(int) = { unused_helper };

static int sum_codes(const struct Table *t)
{
    int s = 0;
    for (int i = 0; i < 12; i++) { s += t->codes[i] * (i + 1); }
    return s;
}

int main(void)
{
    printf("%s|%s|%s\n", outer.a.data, outer.b.data, outer.b.data + 6);
    printf("refcnt %ld %ld kind %d %d\n", outer.a.refcnt, outer.b.refcnt, outer.a.kind, outer.b.kind);
    printf("codes %d\n", sum_codes(&outer.t));
    for (int i = 0; i < 10; i++) { printf("%d ", outer.t.words[i]); }
    printf("\n");
    for (int i = 0; i < 16; i++) { printf("%u ", outer.t.tail[i]); }
    printf("\n");
    int zeros = 0;
    for (int i = 19; i < 32; i++) { zeros += outer.a.data[i] == 0; }
    printf("a tail zeros %d\n", zeros);
    return 0;
}
