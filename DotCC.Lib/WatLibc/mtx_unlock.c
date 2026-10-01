/* mtx_unlock: release the mutex (a recursive one once per lock), waking a waiter if there are any. The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

int mtx_unlock(mtx_t *mtx)
{
    struct __dotcc_mtx *m = (struct __dotcc_mtx *)mtx;
    if ((m->type & mtx_recursive) && --m->count > 0) { return thrd_success; }
    m->owner = 0;
    m->count = 0;
    if (atomic_fetch_sub((atomic_int *)&m->lock, 1) != 1)
    {
        atomic_store((atomic_int *)&m->lock, 0);
        __builtin_wasm_memory_atomic_notify(&m->lock, 1);
    }
    return thrd_success;
}
