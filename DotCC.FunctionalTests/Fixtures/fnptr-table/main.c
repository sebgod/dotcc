/* Taking a function's ADDRESS (`&fn`) — into a file-scope function pointer, a
 * local, a struct field, and a call argument. This only works because dotcc
 * emits user functions as STATIC METHODS of a class (not top-level local
 * functions): a top-level local function can't be addressed (`&fn`) or
 * referenced from a file-scope initializer (CS8801/CS8422/CS8787). The `&fn`
 * resolves across the class boundary via `using static`.
 *
 * NOTE: two richer forms are deferred until later Phase-6 gaps clear (they hit
 * SEPARATE issues, not the emission model): a file-scope struct initializer with
 * a fn-ptr field (`static reg e = { "mul", &mul };`, the exact luaL_Reg shape)
 * and a file-scope ARRAY of bare function pointers (`static binop t[] = { &add };`
 * → GlobalArrayFrom<delegate*> = CS0306). Restore them here once those land. */
#include <stdio.h>

typedef int (*binop)(int, int);

static int add(int a, int b) { return a + b; }
static int mul(int a, int b) { return a * b; }
static int apply(binop f, int a, int b) { return f(a, b); }

static binop g_op = &add;        /* file-scope fn-ptr ← function address */

int main(void) {
    binop local = &mul;
    int r = g_op(3, 4);          /* 7   (global fn-ptr)        */
    r += apply(&add, 10, 20);    /* +30 (&fn as call argument) */
    r += local(6, 7);            /* +42 (local fn-ptr)         */
    r += apply(g_op, 1, 1);      /* +2  (global fn-ptr passed) */
    printf("%d\n", r);           /* 7+30+42+2 = 81 */
    return 0;
}
