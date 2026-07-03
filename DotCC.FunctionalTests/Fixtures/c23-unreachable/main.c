/* C23 unreachable() (<stddef.h> 7.21.1). dotcc gives the UB marker DEFINED
 * behavior — a loud throw via a [DoesNotReturn] helper — and lowers a
 * statement-position call to a real `throw` so the "can't happen" arm of a
 * value-returning function type-checks with no bogus return. This fixture only
 * exercises the NOT-reached path (reaching unreachable() is UB in gcc, so the
 * executed paths are what the oracle can compare). Verified vs gcc -std=c2x. */
#include <stdio.h>
#include <stddef.h>

/* value-returning function whose default arm is provably never taken */
static int weekday_len(int day) {
    switch (day) {
        case 0: return 6;  /* Monday   */
        case 1: return 7;  /* Tuesday  */
        case 2: return 9;  /* Wednesday*/
        default: unreachable();
    }
}

int main(void) {
    int total = 0;
    for (int d = 0; d <= 2; d++) total += weekday_len(d);
    printf("total=%d first=%d\n", total, weekday_len(0));
    return 0;
}
