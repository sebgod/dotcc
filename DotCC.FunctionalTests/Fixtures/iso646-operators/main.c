/* <iso646.h> (C95) — alternative operator spellings. Standard, so both
   oracles validate (gcc -std=c17 and MSVC both provide iso646.h; the
   tokens are NOT C keywords, only available through the header). */

#include <stdio.h>
#include <iso646.h>

int main(void)
{
    int a = 6, b = 3;

    if (a > b and b > 0) printf("and ok\n");
    if (a < b or b > 0)  printf("or ok\n");
    if (not (a == b))    printf("not ok\n");
    if (a not_eq b)      printf("not_eq ok\n");

    printf("bitand = %d\n", a bitand b);   /* 6 & 3 = 2 */
    printf("bitor = %d\n", a bitor b);     /* 6 | 3 = 7 */
    printf("xor = %d\n", a xor b);         /* 6 ^ 3 = 5 */
    printf("compl = %d\n", compl 0);       /* ~0 = -1 */

    int m = 12; m and_eq 10;  printf("and_eq = %d\n", m);   /* 12 & 10 = 8 */
    int n = 4;  n or_eq 1;    printf("or_eq = %d\n", n);    /* 4 | 1 = 5 */
    int x = 5;  x xor_eq 1;   printf("xor_eq = %d\n", x);   /* 5 ^ 1 = 4 */

    return 0;
}
