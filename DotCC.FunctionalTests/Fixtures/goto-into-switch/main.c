/* A goto may jump into a switch's case from outside the switch, and from one case
   into a block of another (C11 6.8.6.1 only forbids jumping into the scope of a
   variably modified type). The two shapes CPython uses: fileio.c's `goto bad_mode`
   from after the loop into an if inside a case, and ceval's PREDICTED(op) label
   inside a TARGET(op) case block, reached from the other cases. */
#include <stdio.h>

static int parse(const char *mode) {
    int rwa = 0, plus = 0, flags = 0;
    const char *s = mode;
    while (*s) {
        switch (*s++) {
        case 'x':
            if (rwa) {
            bad_mode:
                printf("bad mode '%s'\n", mode);
                return -1;
            }
            rwa = 1;
            flags |= 1;
            break;
        case 'r':
            if (rwa)
                goto bad_mode;
            rwa = 1;
            flags |= 2;
            break;
        case '+':
            if (plus)
                goto bad_mode;
            plus = 1;
            flags |= 4;
            break;
        default:
            printf("invalid '%s'\n", mode);
            return -2;
        }
    }
    if (!rwa)
        goto bad_mode;
    return flags;
}

static int run(const int *code, int n) {
    int acc = 0, pc = 0;
    while (pc < n) {
        int op = code[pc++];
        switch (op) {
        case 1: TARGET_ADD: {
            acc += 10;
        PRED_ADD:
            acc += 1;
            break;
        }
        case 2: {
            if (acc > 20) { goto PRED_ADD; }
            acc *= 2;
            break;
        }
        case 3:
            goto PRED_ADD;
        default:
            return -1;
        }
    }
    return acc;
}

int main(void) {
    const char *modes[] = { "x", "r+", "rx", "++", "", "q" };
    for (int i = 0; i < 6; i++) {
        int flags = parse(modes[i]);
        printf("%d\n", flags);
    }
    int prog[] = { 1, 2, 2, 3, 1 };
    int acc = run(prog, 5);
    printf("%d\n", acc);
    return 0;
}
