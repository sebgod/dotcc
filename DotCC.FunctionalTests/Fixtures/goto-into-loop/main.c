/* A goto may jump into a loop body (C11 6.8.6.1): CPython's dtoa.c jumps from
   one digit loop into another at `bump_up`. The body runs on from the label, and
   the loop's own break, continue and condition then behave as usual. */
#include <stdio.h>

static int into_for(int start)
{
    int i, steps = 0;
    if (start > 0) {
        i = start;
        goto inside;
    }
    for (i = 0; i < 5; i++) {
        steps += 10;
      inside:
        steps++;
        if (i == 3) {
            continue;
        }
        if (i == 4) {
            break;
        }
    }
    return steps * 100 + i;
}

static int into_while(int n)
{
    int total = 0;
    goto middle;
    while (n > 0) {
        total += n;
      middle:
        n--;
    }
    return total;
}

static int into_do(int n)
{
    int count = 0;
    if (n > 10) {
        goto resume;
    }
    do {
        count += 2;
      resume:
        count++;
        n++;
    } while (n < 12);
    return count;
}

/* dtoa's shape: the fast loop jumps into the slow loop's rounding step, whose
   `break` then leaves the slow loop. */
static int digits(char *buf, int ilim, int fast)
{
    char *s = buf;
    int i;
    if (fast) {
        for (i = 0;;) {
            *s++ = '7';
            if (++i >= ilim) {
                goto bump_up;
            }
        }
    }
    else {
        for (i = 1;; i++) {
            *s++ = '9';
            if (i == ilim) {
                if (ilim > 1) {
                  bump_up:
                    while (*--s == '9')
                        if (s == buf) {
                            *s = '0';
                            break;
                        }
                    ++*s++;
                }
                break;
            }
        }
    }
    *s = 0;
    return (int)(s - buf);
}

static int with_switch(int start)
{
    int i = 0, acc = 0;
    if (start) {
        goto again;
    }
    for (; i < 6; i++) {
        switch (i % 3) {
        case 0: acc += 1; break;
        case 1: acc += 10; continue;
        default: acc += 100; break;
        }
      again:
        acc += 1000;
    }
    return acc;
}

int main(void)
{
    char buf[8];
    int n;
    printf("%d %d\n", into_for(0), into_for(2));
    printf("%d %d\n", into_while(4), into_while(1));
    printf("%d %d\n", into_do(9), into_do(11));
    n = digits(buf, 3, 0);
    printf("%s %d\n", buf, n);
    n = digits(buf, 2, 1);
    printf("%s %d\n", buf, n);
    printf("%d %d\n", with_switch(0), with_switch(1));
    return 0;
}
