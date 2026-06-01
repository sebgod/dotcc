#include <stdio.h>
#include <stdatomic.h>

/* Phase A2 — the C11 <stdatomic.h> generic functions, lowered onto the seq-cst
 * Atomic.* helpers. Covers atomic_init / load / store / exchange / fetch_* /
 * compare_exchange, the `_explicit` memory-order variants (the order is accepted
 * and treated as seq-cst), atomic_flag, ATOMIC_VAR_INIT / ATOMIC_FLAG_INIT, and
 * the atomic_<T> typedefs. Single-threaded the values are deterministic. */

int main(void) {
    atomic_long counter = 0;
    atomic_init(&counter, 100);
    atomic_fetch_add_explicit(&counter, 5, memory_order_relaxed);   /* 105 */
    long c = atomic_load_explicit(&counter, memory_order_acquire);  /* 105 */
    atomic_store_explicit(&counter, 200, memory_order_release);     /* 200 */

    atomic_uint flags = ATOMIC_VAR_INIT(0);
    atomic_fetch_or(&flags, 0x1);
    atomic_fetch_or(&flags, 0x4);
    atomic_fetch_and(&flags, 0x5);                                  /* 5 & 5 = 5 */
    unsigned f = atomic_load(&flags);

    atomic_int slot;
    atomic_store(&slot, 0);
    int expected = 0;
    _Bool won = atomic_compare_exchange_weak(&slot, &expected, 42); /* 0==0 → 42 */
    int got = atomic_load(&slot);

    atomic_flag lock = ATOMIC_FLAG_INIT;
    int acquired = !atomic_flag_test_and_set(&lock);               /* was clear → acquired */
    atomic_flag_clear_explicit(&lock, memory_order_release);

    atomic_thread_fence(memory_order_seq_cst);
    printf("c=%ld counter=%ld f=%u won=%d got=%d acq=%d lockfree=%d\n",
           c, atomic_load(&counter), f, won, got, acquired, atomic_is_lock_free(&counter));
    return 0;
}
