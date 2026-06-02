/* C switch fall-through: a case section that doesn't end in break/return/… falls
 * into the next case. C# forbids implicit fall-through (CS0163) and forbids the
 * final case falling out of the switch (CS8070). dotcc inserts the explicit jump
 * C performs — `goto case <next>;` / `goto default;` — and a trailing `break;` on
 * the final section; stacked labels and already-terminating sections are left
 * alone. */
#include <stdio.h>

static int classify(int x) {
    int r = 0;
    switch (x) {
        case 0:
        case 1: r += 1;          /* stacked 0/1, then falls through into case 2 */
        case 2: r += 10; break;  /* terminates — no goto */
        case 3: r += 100;        /* falls through into default */
        default: r += 1000;      /* final section -> trailing break */
    }
    return r;
}

int main(void) {
    printf("%d %d %d %d %d\n",
           classify(0), classify(1), classify(2), classify(3), classify(4));
    return 0;   /* 11 11 10 1100 1000 */
}
