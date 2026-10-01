/* mtx_trylock: take the mutex if it is free (or the caller holds a recursive one), else thrd_busy. The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

int mtx_trylock(mtx_t *mtx)
{
    struct __dotcc_mtx *m = (struct __dotcc_mtx *)mtx;
    int me = __dotcc_thread_id();
    if ((m->type & mtx_recursive) && m->owner == me && m->count > 0)
    {
        m->count++;
        return thrd_success;
    }
    int c = 0;
    if (!atomic_compare_exchange_strong((atomic_int *)&m->lock, &c, 1)) { return thrd_busy; }
    m->owner = me;
    m->count = 1;
    return thrd_success;
}
