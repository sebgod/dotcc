/* tss_set: set the running thread's value for the key. The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

int tss_set(tss_t key, void *val)
{
    __dotcc_tss_values.v[*(int *)&key] = val;
    return thrd_success;
}
