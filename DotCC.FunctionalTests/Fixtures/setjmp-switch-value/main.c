/* Value-capturing setjmp via `switch (setjmp(env))`: longjmp carries a
   distinct code that the switch dispatches on. This is real C's "setjmp
   returns twice" — the direct call returns 0, and each longjmp re-enters
   with its value. dotcc lowers it as a goto-restart: the switch re-runs
   with the jump value until a case completes without another longjmp.
   The classic `if (setjmp(env) == 0)` guard can only test zero-vs-nonzero;
   this observes the actual value. */
#include <stdio.h>
#include <setjmp.h>

static jmp_buf env;
static int step = 0;

static void advance(void) {
    step++;
    printf("advance: step=%d\n", step);
    longjmp(env, step * 100);
}

int main(void) {
    switch (setjmp(env)) {
        case 0:
            printf("start\n");
            advance();              /* longjmp(env, 100) */
            break;
        case 100:
            printf("phase one (100)\n");
            advance();              /* longjmp(env, 200) */
            break;
        case 200:
            printf("phase two (200), stop\n");
            break;
        default:
            printf("unexpected\n");
            break;
    }
    printf("end\n");
    return 0;
}
