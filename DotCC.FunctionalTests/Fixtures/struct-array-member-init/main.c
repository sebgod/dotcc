/* Aggregate initializers for structs with ARRAY members — braced, brace-elided
   (C11 6.7.9p20), partial (zero-filled), string literals into char arrays,
   struct / pointer / function-pointer element arrays, 2-D members, designated
   `.member = {…}`, unions (first member), nested structs, compound literals, and
   every storage position: block scope, file scope, static, static local, arrays of
   such structs. Plus access to an array-of-structs member of a global. */
#include <stdio.h>

struct pt { int x, y; };

static int twice(int v) { return 2 * v; }
static int square(int v) { return v * v; }

struct rec {
    int nums[4];
    char tag[6];
    struct pt pts[2];
    const char *names[3];
    int (*ops[2])(int);
    int grid[2][3];
    int last;
};

union word {
    unsigned char bytes[4];
    unsigned int value;
};

struct outer {
    int id;
    struct rec inner;
};

static void show(const char *label, const struct rec *r)
{
    int i, j;
    printf("%s: nums", label);
    for (i = 0; i < 4; i++) printf(" %d", r->nums[i]);
    printf(" | tag \"%s\"", r->tag);
    printf(" | pts (%d,%d) (%d,%d)", r->pts[0].x, r->pts[0].y, r->pts[1].x, r->pts[1].y);
    printf(" | names");
    for (i = 0; i < 3; i++) printf(" %s", r->names[i] ? r->names[i] : "-");
    printf(" | ops");
    for (i = 0; i < 2; i++) {
        if (r->ops[i]) printf(" %d", r->ops[i](5));
        else printf(" -");
    }
    printf(" | grid");
    for (i = 0; i < 2; i++)
        for (j = 0; j < 3; j++) printf(" %d", r->grid[i][j]);
    printf(" | last %d\n", r->last);
}

struct rec g_full = {
    {1, 2, 3, 4}, "hello", {{1, 2}, {3, 4}}, {"a", "b", "c"}, {twice, square},
    {{1, 2, 3}, {4, 5, 6}}, 99
};
static struct rec g_elided = { 1, 2, 3, 4, "abc", 5, 6, 7, 8 };   /* brace elision */
struct rec g_partial = { {7}, "x" };                               /* zero-filled rest */
static struct rec g_designated = {
    .last = 42, .tag = "des", .pts = {{9, 8}}, .nums = {[2] = 5}, .ops = {square},
    .grid = {{0, 0, 1}}
};
struct rec g_table[2] = { { {1}, "t0" }, { {2}, "t1", .last = 0 } };
union word g_word = { {0x78, 0x56, 0x34, 0x12} };
struct outer g_outer = { 7, { {9, 9}, "in", {{5, 5}} } };
struct rec g_zero;

static struct rec make(int base)
{
    struct rec r = { {base, base + 1}, "made", {{base, -base}}, {"m"}, {twice}, {{base}}, base * 10 };
    return r;
}

static int counter(void)
{
    static struct rec s = { {100}, "st", .last = 1 };
    s.nums[0]++;
    s.last *= 2;
    return s.nums[0] + s.last;
}

int main(void)
{
    struct rec local = { {4, 3, 2, 1}, {'c', 'h', 'r'}, {{7, 8}, {9, 10}}, {"x", 0, "z"},
                         {square, twice}, {{9, 8, 7}, {6, 5, 4}}, -1 };
    struct rec elided = { 5, 6, 7, 8, "elide", 1, 2, 3, 4, "p", "q", "r", twice, square,
                          1, 2, 3, 4, 5, 6, 77 };
    struct rec braced_str = { {0}, {"brc"} };
    struct rec exact = { .tag = "sixsi" };
    struct rec arr[2] = { { {1, 1}, "a0" }, { {2, 2}, "a1", .last = 5 } };
    union word w = { {1, 0, 0, 0} };
    struct outer o = { 3, { {4}, "nest", .last = 8 } };
    struct pt p = (struct rec){ {0}, "cl", {{11, 12}} }.pts[0];
    int i;

    show("g_full", &g_full);
    show("g_elided", &g_elided);
    show("g_partial", &g_partial);
    show("g_designated", &g_designated);
    show("g_table[0]", &g_table[0]);
    show("g_table[1]", &g_table[1]);
    show("g_outer.inner", &g_outer.inner);
    show("g_zero", &g_zero);
    show("local", &local);
    show("elided", &elided);
    show("braced_str", &braced_str);
    show("arr[1]", &arr[1]);
    show("o.inner", &o.inner);
    show("make(3)", &(struct rec){0});
    {
        struct rec m = make(3);
        show("make(3)", &m);
    }
    printf("exact tag %.6s, sizeof tag %d\n", exact.tag, (int)sizeof exact.tag);
    printf("g_word bytes %x %x, w value %u\n", g_word.bytes[0], g_word.bytes[3], w.value);
    printf("g_outer.id %d, o.id %d, compound pt (%d,%d)\n", g_outer.id, o.id, p.x, p.y);

    g_full.pts[1].y = 40;          /* array-of-structs member of a global */
    g_zero.pts[0].x = 123;
    g_zero.names[1] = "set";
    printf("g_full.pts[1].y %d, g_zero.pts[0].x %d, g_zero.names[1] %s\n",
           g_full.pts[1].y, g_zero.pts[0].x, g_zero.names[1]);
    for (i = 0; i < 3; i++) printf("counter() = %d\n", counter());
    printf("sizeof(struct rec) %d\n", (int)sizeof(struct rec));
    return 0;
}
