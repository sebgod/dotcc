/* tss_get: the running thread's value for the key. The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

void *tss_get(tss_t key)
{
    return __dotcc_tss_values.v[*(int *)&key];
}
