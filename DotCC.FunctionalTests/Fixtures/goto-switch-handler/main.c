/* A label inside a switch that code before the switch jumps to and that falls
   through into the next case (chibi's VM dispatch reaches call_error_handler so),
   also reached from another case; and a section-start label only code before
   the switch reaches. */
#include <stdio.h>

static int run(const int *ops, int n) {
    int acc = 0, ip = 0, err = 0;
 loop:
    if (ip >= n) {
        return acc;
    }
    if (err) {
        err = 0;
        goto handler;
    }
    switch (ops[ip++]) {
    case 0:
        break;
    handler:
        acc += 100;
        /* fall through */
    case 1:
        acc += 1;
        break;
    case 2:
        err = 1;
        break;
    case 3:
        goto handler;
    default:
        acc -= 1;
        break;
    }
    goto loop;
}

static int start(int k, int jump) {
    int r = 0;
    if (jump) {
        goto second;
    }
    switch (k) {
    case 1:
        r = 10;
        break;
    case 2:
    second:
        r += 20;
        break;
    }
    return r;
}

int main(void) {
    int ops[] = {0, 1, 2, 0, 3, 5, 1};
    printf("%d\n", run(ops, 7));
    printf("%d %d %d\n", start(1, 0), start(2, 0), start(9, 1));
    return 0;
}
