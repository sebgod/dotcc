/* thrd_yield: wasm has no way to give up the processor; a fence. The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

void thrd_yield(void)
{
    atomic_thread_fence(memory_order_seq_cst);
}
