/* Local variables and parameters may shadow enum constants of the same name
 * (legal C). dotcc must resolve a shadowed name to the local/param, NOT rewrite
 * every use to `EnumName.Member` (a const, not an lvalue, and the wrong value).
 * `shade`'s parameter RED shadows the enum constant RED; main's local BLUE
 * shadows the enum constant BLUE; GREEN is never shadowed, so it stays the
 * enum constant. gcc accepts all of this and the oracle confirms the values. */
#include <stdio.h>

enum Color { RED, GREEN, BLUE };   /* 0, 1, 2 */

int shade(int RED) {
    /* RED is the parameter (not enum 0); GREEN is the enum constant (1). */
    return RED + GREEN;
}

int main(void) {
    int BLUE = 10;             /* shadows enum BLUE (2) */
    /* shade(10) = 10 + 1 = 11 ; BLUE = 10 ; GREEN = enum constant 1 */
    printf("%d %d %d\n", shade(BLUE), BLUE, GREEN);
    return 0;
}
