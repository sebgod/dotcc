/* cnd_destroy: nothing to release. The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

void cnd_destroy(cnd_t *cond)
{
    (void)cond;
}
