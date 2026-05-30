/* `extern` declarations: g_count and bump are DEFINED in a_defs.c. extern
 * tells the compiler "this exists elsewhere" — no storage / body emitted here.
 * dotcc compiles both TUs into one program, so the references resolve. */
#include <stdio.h>

extern int g_count;          /* declared here, defined in a_defs.c */
extern void bump(int by);    /* prototype for the function over there */

int main(void) {
    bump(10);
    bump(32);
    printf("g_count = %d\n", g_count);
    return 0;
}
