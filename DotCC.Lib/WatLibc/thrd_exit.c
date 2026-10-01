/* thrd_exit: end the running thread with res, unwinding to its entry; on the main thread, exit(res). The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

void thrd_exit(int res)
{
    if (__dotcc_self == NULL) { exit(res); }
    __dotcc_self->result = res;
    longjmp(__dotcc_self->exit, 1);
}
