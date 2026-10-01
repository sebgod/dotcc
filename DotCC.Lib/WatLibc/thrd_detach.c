/* thrd_detach: nobody will join the thread (its descriptor and stack stay, as the heap never frees). The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

int thrd_detach(thrd_t thr)
{
    (*(struct __dotcc_thread **)&thr)->detached = 1;
    return thrd_success;
}
