#include <stdio.h>
#include <string.h>

/* Declarators the per-shape grammar had no rule for (GH #333). */

static int add(int a, int b) { return a + b; }
static int sub(int a, int b) { return a - b; }

/* A function returning a pointer to an array. */
static int grid[2][3] = { { 1, 2, 3 }, { 4, 5, 6 } };
static int (*row(int i))[3] { return &grid[i]; }

/* A function returning a function pointer, chosen by a function pointer parameter. */
static int (*pick(int which, int (*fallback)(int, int)))(int, int)
{
    return which == 0 ? add : which == 1 ? sub : fallback;
}

/* A pointer to a function returning a pointer to an array. */
static int (*(*rowfn)(int))[3] = row;

/* An array of pointers to arrays, and a pointer to a pointer to a function. */
static int (*rows[2])[3] = { &grid[1], &grid[0] };

/* Parameters: a pointer to an array, a multi-dimensional array, an unnamed function pointer. */
static int sum_rows(int (*m)[3], int n)
{
    int s = 0;
    for (int i = 0; i < n; i++) { for (int j = 0; j < 3; j++) { s += m[i][j]; } }
    return s;
}
static int sum_grid(int m[2][3]) { return sum_rows(m, 2); }
static int apply(int (*)(int, int), int, int);
static int apply(int (*f)(int, int), int a, int b) { return f(a, b); }

struct Ops
{
    int (*table[2])(int, int);
    int (*cell)[3];
    char *(names[2]);
};

typedef int (*BinOp)(int, int);
typedef int Row[3], (*RowPtr)[3], *(*Chooser(int))[3];

int main(void)
{
    int (*p)[3] = row(1), *q = grid[0], r[2] = { 7, 8 }, (*f)(int, int) = pick(1, add);
    printf("%d %d %d %d\n", (*p)[0], q[2], r[1], f(9, 4));

    int (**pf)(int, int) = &f;
    printf("%d\n", (*pf)(20, 1));

    printf("%d %d\n", (*rowfn(0))[1], (*rows[0])[2]);
    printf("%d %d\n", sum_grid(grid), apply(pick(2, add), 30, 12));

    struct Ops ops = { { add, sub }, &grid[1], { "add", "sub" } };
    printf("%d %d %d %s\n", ops.table[0](1, 2), ops.table[1](1, 2), (*ops.cell)[1], ops.names[1]);

    /* Abstract declarators: casts, sizeof, compound literals. */
    void *raw = grid;
    int (*cast)[3] = (int (*)[3])raw;
    BinOp g = (int (*)(int, int))sub;
    printf("%d %d\n", cast[1][2], g(5, 3));
    printf("%d %d %d\n", (int)sizeof(int[4]), (int)sizeof(int (*)[4]) == (int)sizeof(void *), (int)sizeof(char *[3]) == 3 * (int)sizeof(char *));
    printf("%d\n", (int)(sizeof(Row) / sizeof(int)));

    RowPtr rp = &grid[0];
    int total = 0;
    int *lit = (int[]){ 10, 20, 30 };
    for (int i = 0; i < 3; i++) { total += lit[i] + (*rp)[i]; }
    printf("%d\n", total);

    const char *const *const names = (const char *const[]){ "x", "y" };
    printf("%s%s\n", names[0], names[1]);
    return 0;
}
