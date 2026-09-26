/* Raw (un-typedef'd) function-pointer declarators in every storage position:
   file-scope variables (plain / static / extern / initialized), arrays of
   function pointers (sized, size-from-initializer, zero-filled), block-scope
   arrays, block-scope static pointers and tables, and an array-of-fn-ptrs
   struct member — each the spelling a typedef would otherwise hide. */
#include <stdio.h>

static int twice(int x) { return 2 * x; }
static int square(int x) { return x * x; }
static int negate(int x) { return -x; }
static void hello(void) { printf("hello from a void(*)(void)\n"); }

int (*hook)(int);                                  /* file scope, zero-initialized */
static int (*op)(int) = twice;                     /* static + initializer */
extern int (*late)(int);                           /* declared here ... */
int (*late)(int) = square;                         /* ... defined here */
static void (*greet)(void) = hello;                /* () tail */

static int (*ops[])(int) = { twice, square, negate };   /* size from initializer */
int (*sparse[4])(int) = { square };                      /* zero-filled tail */
static int (*unset[2])(int);                             /* no initializer */

struct vtable {
    const char *name;
    int (*fns[2])(int);
};

static int apply_all(int v)
{
    int (*local[])(int) = { negate, twice };
    int (*fixed[2])(int);
    int i;

    fixed[0] = square;
    fixed[1] = local[0];
    for (i = 0; i < 2; i++)
        v = local[i](v) + fixed[i](v);
    return v;
}

static int counter(void)
{
    static int (*step)(int) = twice;               /* static local */
    static int (*chain[])(int) = { square, negate };
    static int calls;
    calls++;
    return step(calls) + chain[calls % 2](calls);
}

int main(void)
{
    int i;
    struct vtable vt;

    printf("hook is %s\n", hook == NULL ? "NULL" : "set");
    hook = negate;
    printf("hook(5) = %d, op(5) = %d, late(5) = %d\n", hook(5), op(5), late(5));
    greet();

    for (i = 0; i < (int)(sizeof ops / sizeof ops[0]); i++)
        printf("ops[%d](7) = %d\n", i, ops[i](7));
    for (i = 0; i < 4; i++)
        printf("sparse[%d] is %s\n", i, sparse[i] ? "set" : "NULL");
    printf("unset[1] is %s\n", unset[1] == NULL ? "NULL" : "set");

    printf("apply_all(3) = %d\n", apply_all(3));
    for (i = 0; i < 3; i++)
        printf("counter() = %d\n", counter());

    vt.name = "math";
    vt.fns[0] = square;
    vt.fns[1] = twice;
    printf("%s: %d %d\n", vt.name, vt.fns[0](4), vt.fns[1](4));
    return 0;
}
