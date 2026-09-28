/* Function-typed parameters and pointers to function pointers (GH #220).
   A parameter declared with a function type is adjusted to a pointer to that
   function (C11 6.7.6.3p8): CPython's `int siftup(PyListObject *, Py_ssize_t)`
   (_heapqmodule.c) and the parenthesized `void *(func)(Parser *)` (pegen.h).
   `int (**func)(void *)` (ceval_gil.c) is a pointer to a function pointer, and
   `static int (name)(...)` a parenthesized function name. */
#include <stdio.h>

static int twice(int x) { return 2 * x; }
static int square(int x) { return x * x; }

static int apply(int f(int), int v) { return f(v); }
static int apply_paren(int (f)(int), int v) { return f(v); }
static int apply_const(int (*const f)(int), int v) { return f(v); }

static int (named)(int x) { return x + 100; }

static void pick(int want_square, int (**slot)(int)) {
    *slot = want_square ? square : twice;
}

struct Table { int (**entry)(int); };

int main(void) {
    printf("%d %d %d %d\n", apply(twice, 5), apply_paren(square, 5), apply_const(named, 5), named(1));
    int (*chosen)(int) = twice;
    int (**slot)(int) = &chosen;
    pick(1, slot);
    printf("%d\n", chosen(7));
    struct Table t = { &chosen };
    pick(0, t.entry);
    printf("%d %d\n", (*t.entry)(9), (**t.entry)(10));
    return 0;
}
