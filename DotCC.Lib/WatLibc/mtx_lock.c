/* mtx_lock: take the mutex, sleeping on it while another thread holds it (Drepper's futex mutex: 0 free, 1 held, 2 held with waiters); a recursive mutex its owner takes again. The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

int mtx_lock(mtx_t *mtx)
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
            __builtin_wasm_memory_atomic_wait32(&m->lock, 2, -1);
            c = atomic_exchange((atomic_int *)&m->lock, 2);
        }
    }
    m->owner = me;
    m->count = 1;
    return thrd_success;
}
