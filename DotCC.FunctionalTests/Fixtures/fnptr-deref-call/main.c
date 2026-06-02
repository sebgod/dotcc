// Calling through a DEREFERENCED function pointer. C's (*fp)(args) is a no-op
// deref that decays back to fp, so it's identical to fp(args). dotcc must drop
// the `*` — C# calls function pointers directly and rejects `*fp` (CS0193).
// Covers a local, a function parameter, and a struct field. Lua uses this shape
// pervasively ((*g->frealloc)(...), (*ci->u.c.k)(...), (*cf)(L), ...).

#include "stdio.h"

typedef int (*BinOp)(int, int);
typedef struct { BinOp op; const char *name; } Entry;

static int add(int a, int b) { return a + b; }
static int mul(int a, int b) { return a * b; }

// fn-ptr parameter, invoked via the deref form.
static int apply(BinOp f, int x, int y) { return (*f)(x, y); }

int main(void) {
    BinOp f = add;
    int r1 = (*f)(2, 3);            // local fn-ptr

    Entry e;
    e.op = mul;
    e.name = "mul";
    int r2 = (*(e.op))(4, 5);       // struct-field fn-ptr

    int r3 = apply(add, 10, 20);    // param fn-ptr (inside apply)

    printf("%d %d %d %s\n", r1, r2, r3, e.name);
    return 0;
}
