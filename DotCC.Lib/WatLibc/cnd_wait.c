/* cnd_wait: release the mutex, sleep until signalled, take the mutex again (a spurious wake-up is allowed, as C11 allows it). The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

int cnd_wait(cnd_t *cond, mtx_t *mtx)
{
    struct __dotcc_cnd *c = (struct __dotcc_cnd *)cond;
    int seq = atomic_load((atomic_int *)&c->seq);
    mtx_unlock(mtx);
    __builtin_wasm_memory_atomic_wait32(&c->seq, seq, -1);
    mtx_lock(mtx);
    return thrd_success;
}
