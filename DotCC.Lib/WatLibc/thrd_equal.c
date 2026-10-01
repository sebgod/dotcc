/* thrd_equal: whether the two name the same thread. The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

int thrd_equal(thrd_t a, thrd_t b)
{
    return *(struct __dotcc_thread **)&a == *(struct __dotcc_thread **)&b;
}
