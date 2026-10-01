/* cnd_broadcast: wake every waiter. The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

int cnd_broadcast(cnd_t *cond)
{
    struct __dotcc_cnd *c = (struct __dotcc_cnd *)cond;
    atomic_fetch_add((atomic_int *)&c->seq, 1);
    __builtin_wasm_memory_atomic_notify(&c->seq, 0xFFFFFFFFu);
    return thrd_success;
}
