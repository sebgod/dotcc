/* mtx_destroy: nothing to release. The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

void mtx_destroy(mtx_t *mtx)
{
    (void)mtx;
}
