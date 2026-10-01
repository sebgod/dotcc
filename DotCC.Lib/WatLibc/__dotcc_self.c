/* __dotcc_self: the running thread's descriptor, NULL on the main thread. The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

_Thread_local struct __dotcc_thread *__dotcc_self;
