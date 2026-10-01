/* mtx_timedlock: mtx_lock, giving up with thrd_timedout at the TIME_UTC deadline. The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

int mtx_timedlock(mtx_t *mtx, const struct timespec *ts)
{
    struct __dotcc_mtx *m = (struct __dotcc_mtx *)mtx;
    int me = __dotcc_thread_id();
    if ((m->type & mtx_recursive) && m->owner == me && m->count > 0)
    {
        m->count++;
        return thrd_success;
    }
    int c = 0;
    if (!atomic_compare_exchange_strong((atomic_int *)&m->lock, &c, 1))
    {
        if (c != 2) { c = atomic_exchange((atomic_int *)&m->lock, 2); }
        while (c != 0)
        {
            long long left = __dotcc_ns_until(ts);
            if (left <= 0) { return thrd_timedout; }
            __builtin_wasm_memory_atomic_wait32(&m->lock, 2, left);
            c = atomic_exchange((atomic_int *)&m->lock, 2);
        }
    }
    m->owner = me;
    m->count = 1;
    return thrd_success;
}
