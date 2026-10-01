/* cnd_init: a condition nobody waits on. The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

int cnd_init(cnd_t *cond)
{
    struct __dotcc_cnd *c = (struct __dotcc_cnd *)cond;
    c->seq = 0;
    c->waiters = 0;
    return thrd_success;
}
