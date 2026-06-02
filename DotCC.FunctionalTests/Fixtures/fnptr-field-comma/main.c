#include <stdio.h>

/* A union with a function-pointer member, reached through a POINTER TYPEDEF
   base. dotcc lowers C's comma operator to a C# value-tuple, and a pointer /
   function-pointer can't be a tuple element — so the field's type must be
   synthesized through the typedef'd-pointer chain (`BoxPtr` -> `Box*`) for the
   tuple lowering to round-trip it through nint. Mirrors Lua's
   `check_exp(c, e)` = `(lua_assert(c), e)` reaching a `lua_CFunction` union
   member via a `StkId`-style alias. */
typedef int (*Op)(int);

typedef union Val {
    int i;
    Op fn;
} Val;

typedef struct Box { Val v; } Box;
typedef Box *BoxPtr;          /* the pointer typedef that exercises the chain */

static int dbl(int x) { return x * 2; }
static int inc(int x) { return x + 1; }

/* check_exp-shaped: (discarded-void, fn-ptr value) through a BoxPtr base. */
#define pick(b) ((void)0, ((b)->v).fn)

/* Lua's `LUAI_TRY` spells a direct fn-ptr call as `((f)(args))` — a
   parenthesised bare-name callee, which is cast-ambiguous in C#. */
static int call_through(Op f, int x) { return (f)(x); }

int main(void) {
    Box b1, b2;
    b1.v.fn = dbl;
    b2.v.fn = inc;
    BoxPtr p1 = &b1, p2 = &b2;
    Op a = pick(p1);
    Op c = pick(p2);
    printf("%d %d\n", call_through(a, 21), call_through(c, 9));

    /* a POINTER pre-increment discarded in a comma (the comma's value is 1) */
    char buf[] = "xy";
    char *m = buf;
    int r = ((void)(++m), 1);
    printf("%d %c\n", r, *m);
    return 0;
}
