/* Function declarators are init-declarators (GH #223, C11 6.7.6.3): a prototype
   is an ordinary declaration, so a list may declare several functions (and mix
   in objects), a typedef may name a function TYPE (CPython's pycore_lock.h
   `typedef int _Py_once_fn_t(void *arg);`), and a function may be declared at
   block scope (pythonrun.c), plain or `extern`. */
#include <stdio.h>

typedef int once_fn_t(void *arg);
typedef int binop_t(int, int), *int_ptr;

int twice(int), thrice(int), (add)(int, int);
static int apply(once_fn_t *fn, void *arg);
int (*pick(int which))(int);

static int counter = 0;
static int bump(void *arg) { counter += *(int *)arg; return counter; }

int twice(int x) { return 2 * x; }
int thrice(int x) { return 3 * x; }
int (add)(int a, int b) { return a + b; }
static int apply(once_fn_t *fn, void *arg) { return fn(arg); }
int (*pick(int which))(int) { return which ? thrice : twice; }

static int combine(binop_t *op, int a, int b) { return op(a, b); }

static int read_total(void)
{
    extern int total;
    return total;
}

int total = 42;

int main(void)
{
    int step = 5;
    int_ptr sp = &step;
    extern int twice(int);
    int helper(int), unused = 0;
    int first = apply(bump, sp);
    int second = apply(bump, &step);
    printf("%d %d %d\n", first, second, twice(4));
    printf("%d %d %d\n", pick(0)(7), pick(1)(7), combine(add, 2, 3));
    printf("%d %d %d\n", helper(10), read_total(), unused);
    return 0;
}

int helper(int x) { return x + 1; }
