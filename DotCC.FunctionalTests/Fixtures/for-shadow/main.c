/* for-loop variable shadowing — the for-init declaration scopes to the whole
 * for-statement (init + cond + post + body) in C, so a `for (int i …)` that
 * shadows an outer `i` must NOT leak: the outer `i` is untouched after the
 * loop, and a body-block local can shadow the loop variable in turn. dotcc
 * gives the for-init its own scope frame (the ScopeEnter marker after `for (`)
 * so all three `i`s are alpha-renamed apart. gcc is the oracle. */
#include <stdio.h>

int main(void) {
    int i = 100;

    int sum = 0;
    for (int i = 0; i < 5; i++) {
        sum += i;            /* the loop's i: 0+1+2+3+4 = 10 */
    }
    printf("inner sum = %d\n", sum);
    printf("outer i = %d\n", i);          /* 100 — the for's i didn't leak */

    for (int i = 10; i < 13; i++) {
        int i = 7;                         /* body shadows the loop variable */
        printf("body i = %d\n", i);        /* 7, three times */
    }
    printf("outer i still = %d\n", i);    /* 100 */

    return 0;
}
