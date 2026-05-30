/* In C, the relational (`< > <= >= == !=`) and logical (`&& ||`) operators
 * yield `int` 0/1 — NOT a bool — so their result is usable directly in any
 * integer position: assignment, arithmetic, function argument, and `return`
 * from an int-returning function. dotcc lowers each such result to the
 * integer-typed `CBool` (CBool -> int carries it into these positions; a
 * Cond.B(CBool) overload carries it into conditionals). C# `<`/`&&` produce
 * `bool`, which can't land in those slots, so this used to emit code that
 * didn't compile. gcc confirms the values. */
#include <stdio.h>

int is_positive(int v) {
    return v > 0;            /* relational result returned from an int function */
}

int main(void) {
    int a = 5, b = 3, c = 0;

    int gt    = a > b;                        /* 1 */
    int eq    = (a == b);                     /* 0 */
    int ne    = (a != b);                     /* 1 */
    int cnt   = (a > 0) + (b > 0) + (c > 0);  /* 1 + 1 + 0 = 2 */
    int land  = a && b;                       /* 1 */
    int lor   = c || a;                       /* 1 */
    int chain = (a > b) == 1;                 /* (1) == 1 -> 1 */

    printf("%d %d %d %d %d %d %d\n", gt, eq, ne, cnt, land, lor, chain);
    printf("%d %d\n", is_positive(5), is_positive(-2));   /* 1 0 */
    return 0;
}
