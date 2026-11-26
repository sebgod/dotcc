/* GNU __attribute__((...)) in declaration-specifier position, and GNU __thread.
   noreturn and always_inline lower (the noreturn function needs no return
   after its call); the rest (format, noinline, unused, deprecated, aligned)
   are accepted and ignored, as clang ignores an attribute it does not know. */
#include <stdio.h>
#include <stdlib.h>

static __attribute__((noreturn)) void die(const char *why);
__attribute__((noinline)) static int add(int a, int b) { return a + b; }
static inline __attribute__((always_inline, __unused__)) int twice(int x) { return 2 * x; }
int __attribute__((format(printf, 1, 2))) say(const char *fmt, ...);
__attribute__((__noreturn__)) static void die2(void) { exit(0); }

static __thread int per_thread = 0;

static int pick(int v) {
    if (v >= 0) return v * 10;
    die("negative");
}

static void die(const char *why) {
    printf("die: %s\n", why);
    exit(1);
}

int main(void) {
    per_thread += add(2, 3);
    printf("%d %d %d\n", per_thread, twice(4), pick(7));
    /* The noreturn paths are compiled, not taken: exit() would end the
       in-process fixture host. */
    if (per_thread < 0) die2();
    return 0;
}
