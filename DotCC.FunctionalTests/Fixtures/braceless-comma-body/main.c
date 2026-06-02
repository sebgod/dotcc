/* A braceless `if`/`else` (or loop) body that is a COMMA expression expanding to
 * multiple statements — Lua's `luaL_addchar(B,c)` = `((void)(...), (...))`. dotcc
 * lowers a comma statement to several C# statements; without a wrapping block the
 * first would become the if-body and the rest (plus any `else`) would detach
 * (the `'else' cannot start a statement` family). dotcc now block-wraps a
 * multi-statement comma so it stays ONE statement. */
#include <stdio.h>

#define addtwo(a, b)  ((a) += 1, (b) += 10)

int main(void) {
    int x = 0, y = 0, n = 0;
    if (n == 0)
        addtwo(x, y);            /* braceless comma body: x=1, y=10 */
    else
        n = 99;
    printf("%d %d %d\n", x, y, n);   /* 1 10 0 */

    while (x < 3)
        (x += 1, n += 1);        /* braceless comma loop body */
    printf("%d %d\n", x, n);         /* 3 2 */
    return 0;
}
