/* Value-capturing setjmp bound to a variable, dispatched with `if`:
   `int r = setjmp(env); if (r == …) …`. The value longjmp passes is
   observed, which the zero-vs-nonzero `if (setjmp(env))` guard can't do.
   dotcc lowers it as a goto-restart over the rest of the block: the block
   re-runs with r holding the jump value, so the matching arm is reached. */
#include <stdio.h>
#include <setjmp.h>

static jmp_buf env;

static void raise_error(int code) {
    printf("raising %d\n", code);
    longjmp(env, code);
}

int main(void) {
    int r = setjmp(env);
    if (r == 0) {
        printf("protected block\n");
        raise_error(5);
        printf("unreachable\n");
    } else if (r == 5) {
        printf("handled error 5\n");
    } else {
        printf("other error %d\n", r);
    }
    printf("after (r=%d)\n", r);
    return 0;
}
