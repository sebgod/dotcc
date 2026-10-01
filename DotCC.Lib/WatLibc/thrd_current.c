/* thrd_current: the running thread; the main thread's is a descriptor of its own. The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

thrd_t thrd_current(void)
{
    static struct __dotcc_thread main_thread;
    thrd_t r;
    *(struct __dotcc_thread **)&r = __dotcc_self ? __dotcc_self : &main_thread;
    return r;
}
