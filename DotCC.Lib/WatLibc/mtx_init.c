/* mtx_init: a free mutex of the given type (plain, recursive, timed). The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

int mtx_init(mtx_t *mtx, int type)
{
    struct __dotcc_mtx *m = (struct __dotcc_mtx *)mtx;
    m->lock = 0;
    m->type = type;
    m->owner = 0;
    m->count = 0;
    return thrd_success;
}
