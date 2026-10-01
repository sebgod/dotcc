/* cnd_timedwait: cnd_wait, giving up with thrd_timedout at the TIME_UTC deadline (the mutex taken again either way). The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

int cnd_timedwait(cnd_t *cond, mtx_t *mtx, const struct timespec *ts)
{
    struct __dotcc_cnd *c = (struct __dotcc_cnd *)cond;
    int seq = atomic_load((atomic_int *)&c->seq);
    long long left = __dotcc_ns_until(ts);
    mtx_unlock(mtx);
    int r = left <= 0 ? 2 : __builtin_wasm_memory_atomic_wait32(&c->seq, seq, left);
    mtx_lock(mtx);
    return r == 2 ? thrd_timedout : thrd_success;
}
