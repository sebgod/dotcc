/* thrd_join: wait for the thread to finish, and take its result. The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

int thrd_join(thrd_t thr, int *res)
{
    struct __dotcc_thread *t = *(struct __dotcc_thread **)&thr;
    while (atomic_load((atomic_int *)&t->state) == 0)
    {
        __builtin_wasm_memory_atomic_wait32(&t->state, 0, -1);
    }
    if (res) { *res = t->result; }
    return thrd_success;
}
