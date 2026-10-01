/* call_once: run func the first time, every other caller waiting until it has returned (0 not run, 1 running, 2 done). The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

void call_once(once_flag *flag, void (*func)(void))
{
    int expected = 0;
    if (atomic_compare_exchange_strong((atomic_int *)flag, &expected, 1))
    {
        func();
        atomic_store((atomic_int *)flag, 2);
        __builtin_wasm_memory_atomic_notify(flag, 0xFFFFFFFFu);
        return;
    }
    while (atomic_load((atomic_int *)flag) != 2)
    {
        __builtin_wasm_memory_atomic_wait32(flag, 1, -1);
    }
}
