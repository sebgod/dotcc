#include <stdio.h>

/* C11 `_Atomic` type-specifier (phase A1) — seq-cst load / store / read-modify-write
 * via the Interlocked-backed Atomic.* helpers. Single-threaded the values are
 * deterministic; the point is that every access to an atomic object routes through
 * the atomic primitives. Covers a scalar local, the `_Atomic(T)` paren form, and an
 * atomic struct member through a pointer. */

struct Stats { _Atomic long hits; int other; };

static void record(struct Stats *s, long n) {
    s->hits += n;            /* atomic fetch-add on a member through a pointer */
}

int main(void) {
    _Atomic int counter = 0;
    counter = 5;             /* atomic store */
    counter += 3;            /* atomic fetch-add (compound assign yields new value) */
    counter++;               /* atomic post-increment */
    int snap = counter;      /* atomic load */

    _Atomic(unsigned) mask = 0;
    mask |= 0x2;             /* atomic bitwise-or */
    mask |= 0x8;

    struct Stats s;
    s.hits = 0;
    s.other = 0;
    record(&s, 10);
    record(&s, 7);

    printf("counter=%d snap=%d mask=%u hits=%ld\n", counter, snap, mask, s.hits);
    return 0;
}
