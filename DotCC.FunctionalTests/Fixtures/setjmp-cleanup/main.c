/* setjmp / longjmp error-cleanup ladder. The canonical real-world use
   of <setjmp.h>: deeply-nested code calls longjmp to unwind past
   several layers without explicit error-return plumbing. dotcc
   implements this via .NET exceptions — longjmp throws a tagged
   exception and the emitter rewrites `if (setjmp(env) == 0) …` into
   a try/catch when. */

#include <stdio.h>
#include <setjmp.h>

jmp_buf err_env;

void deep_layer_3(int x) {
    if (x < 0) {
        printf("layer3: bailing with %d\n", x);
        longjmp(err_env, 99);
    }
    printf("layer3: ok x=%d\n", x);
}

void deep_layer_2(int x) {
    printf("layer2: enter\n");
    deep_layer_3(x);
    printf("layer2: exit\n");
}

void deep_layer_1(int x) {
    printf("layer1: enter\n");
    deep_layer_2(x);
    printf("layer1: exit\n");
}

int main(void) {
    /* First scenario: success path. */
    if (setjmp(err_env) == 0) {
        deep_layer_1(5);
        printf("main: normal completion\n");
    } else {
        printf("main: ERROR (unexpected on success path)\n");
    }

    printf("---\n");

    /* Second scenario: deep longjmp unwinds three frames. */
    if (setjmp(err_env) == 0) {
        deep_layer_1(-1);
        printf("main: should not reach here\n");
    } else {
        printf("main: caught error\n");
    }

    return 0;
}
