/* cnd_signal: wake one waiter (a new sequence number, so a waiter that has not slept yet does not). The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

int cnd_signal(cnd_t *cond)
{
    struct __dotcc_cnd *c = (struct __dotcc_cnd *)cond;
    atomic_fetch_add((atomic_int *)&c->seq, 1);
    __builtin_wasm_memory_atomic_notify(&c->seq, 1);
    return thrd_success;
}
