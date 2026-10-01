/* thrd_sleep: sleep the whole duration (no signal interrupts it), waiting on a word nobody wakes. The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

int thrd_sleep(const struct timespec *duration, struct timespec *remaining)
{
    int never = 0;
    long long ns = (long long)duration->tv_sec * 1000000000LL + duration->tv_nsec;
    if (ns > 0) { __builtin_wasm_memory_atomic_wait32(&never, 0, ns); }
    if (remaining) { remaining->tv_sec = 0; remaining->tv_nsec = 0; }
    return 0;
}
