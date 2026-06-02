/* A comma operator in value position lowers to a C# tuple `(a, b).Item2`. A
 * pointer / function-pointer operand can't be a ValueTuple type argument
 * (CS0306), so dotcc round-trips it through nint. This now also covers a pointer
 * TYPEDEF (NodeRef -> Node*, like Lua's StkId -> StackValue*) and a
 * function-pointer TYPEDEF (CFunc -> delegate*<...>, like lua_CFunction), neither
 * of which ends in a literal `*` — the shape behind Lua's
 * `check_exp(c, e) = (lua_assert(c), e)` when `e` is such a value. */
#include <stdio.h>

typedef struct Node { int v; } Node;
typedef Node *NodeRef;          /* pointer typedef (cf. Lua's StkId) */
typedef int (*CFunc)(int);      /* function-pointer typedef (cf. lua_CFunction) */

static int dbl(int x) { return x * 2; }

int main(void) {
    Node    n  = { 7 };
    NodeRef np = &n;
    CFunc   cf = dbl;
    int     g  = 0;

    /* leading operand is a (non-void) assignment, so the comma is a value tuple;
     * the comma's VALUE is a pointer-typedef / fn-ptr-typedef variable. */
    NodeRef p = (g = 1, np);    /* value is NodeRef  -> nint round-trip */
    CFunc   f = (g = 2, cf);    /* value is CFunc     -> nint round-trip */

    printf("%d %d %d\n", p->v, f(20), g);   /* 7 40 2 */
    return 0;
}
