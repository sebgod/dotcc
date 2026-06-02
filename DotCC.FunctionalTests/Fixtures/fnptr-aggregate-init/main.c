/* A bare function name used as a value decays to a pointer-to-function
 * (C §6.3.2.1: function-to-pointer conversion). dotcc emits the `&` C# requires
 * at every such value position. Before this, only call arguments and scalar
 * fn-ptr decl-inits got the `&`; aggregate initializers and assignments did not,
 * which is exactly Lua's ubiquitous `luaL_Reg` tables (`{"name", cfunc}`).
 *
 * Covers: struct-array element initializer, C99 designated `.field =`, and a
 * plain assignment to a fn-ptr lvalue. */
#include <stdio.h>

typedef int (*fn)(int);
typedef struct { const char *name; fn func; } reg;

static int add1(int x) { return x + 1; }
static int dbl(int x)  { return x * 2; }
static int neg(int x)  { return -x; }

/* struct-array initializer: each element's `func` is a bare function name —
 * `{"add1", add1}` must lower to `func = &add1`. */
static const reg tbl[] = {
    {"add1", add1},
    {"dbl",  dbl},
};

int main(void) {
    int sum = 0;

    /* call through the table's fn-ptr field */
    for (int i = 0; i < 2; i++)
        sum += tbl[i].func(i + 10);      /* add1(10)=11, dbl(11)=22 -> 33 */

    /* C99 designated initializer: `.func = neg` -> `func = &neg`. Read the
     * decayed field back into a local fn-ptr, then call that (a direct call
     * through a parenthesized simple-member callee, `(r.func)(5)`, is a separate
     * C#-cast-ambiguity gap noted in examples/lua/ROADMAP.md). */
    reg r = { .name = "neg", .func = neg };
    fn h = r.func;
    sum += h(5);                         /* neg(5) = -5 -> 28 */

    /* plain assignment of a bare fn name to a fn-ptr lvalue: `g = add1;` */
    fn g;
    g = add1;
    sum += g(100);                       /* 101 -> 129 */

    printf("%d\n", sum);
    return 0;
}
