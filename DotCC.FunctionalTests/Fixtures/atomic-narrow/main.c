#include <stdatomic.h>
#include <stdint.h>
#include <stdio.h>

/* 1- and 2-byte atomics touch only their own bytes. CPython's _PyOnceFlag is a
 * uint8_t beside other fields, updated through a cast to _Atomic(uint8_t) *: a
 * compare-exchange done 4 bytes wide compared the neighbours too, never
 * succeeded, and so spun forever at interpreter exit. */

struct once {
    uint8_t before;
    uint8_t v;
    uint16_t h;
    uint8_t after[4];
};

int main(void) {
    struct once o = { 0xA1, 0, 0x1234, { 0xB1, 0xB2, 0xB3, 0xB4 } };

    uint8_t want = 0;
    int won = atomic_compare_exchange_strong((_Atomic(uint8_t) *)&o.v, &want, 1);
    want = 0;   /* stale now: fails and reports the 1 it found */
    int again = atomic_compare_exchange_strong((_Atomic(uint8_t) *)&o.v, &want, 2);
    printf("won=%d again=%d want=%u v=%u\n", won, again, want, o.v);

    uint16_t old = atomic_fetch_add((_Atomic(uint16_t) *)&o.h, 0xF000);   /* wraps in 16 bits */
    uint8_t prev = atomic_exchange((_Atomic(uint8_t) *)&o.after[1], 0x7F);
    printf("old=%#x h=%#x prev=%#x\n", old, o.h, prev);
    printf("before=%#x after=%x %x %x %x\n", o.before, o.after[0], o.after[1], o.after[2], o.after[3]);

    _Atomic char c = 'a';
    c += 2;
    _Atomic unsigned char u = 250;
    u += 10;   /* 260 wraps to 4 */
    atomic_bool flag = 0;
    int was = atomic_exchange(&flag, 1);
    printf("c=%c u=%u was=%d flag=%d lockfree=%d\n", c, u, was, (int)atomic_load(&flag),
           atomic_is_lock_free(&flag));
    return 0;
}
