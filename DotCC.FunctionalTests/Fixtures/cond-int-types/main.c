/* In C a scalar of ANY integer type is truthy when non-zero. dotcc wraps a
 * controlling expression in Cond.B(...); the helper needs an exact overload for
 * every lowered numeric type. Without them, a `byte` / `uint` / `long` /
 * `ulong` / `short` argument (e.g. Lua's `if (ls->allowhook)`, where allowhook
 * is `lu_byte`) is convertible to BOTH a built-in numeric overload AND `CBool`
 * (one user-defined step), so the Cond.B call is ambiguous (CS0121). An exact
 * overload per type beats the user-defined CBool path and resolves it. */
#include <stdio.h>

int main(void) {
    unsigned char  b  = 3;     /* byte   */
    unsigned int   u  = 2;     /* uint   */
    long           l  = -5;    /* long   */
    unsigned long  ul = 100;   /* ulong  */
    short          s  = 0;     /* short  */
    unsigned int   z  = 0;

    int count = 0;
    if (b)  count += 1;
    if (u)  count += 10;
    if (l)  count += 100;
    if (ul) count += 1000;
    if (s)  count += 10000;    /* 0 -> skip */
    if (z)  count += 100000;   /* 0 -> skip */

    printf("%d\n", count);     /* 1 + 10 + 100 + 1000 = 1111 */
    return 0;
}
