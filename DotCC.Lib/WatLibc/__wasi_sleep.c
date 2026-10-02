/* __wasi_sleep: block for a while, through poll_oneoff with one subscription, to the monotonic
   clock (see posix_impl.h). The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>
#include <posix_impl.h>

int __wasi_sleep(unsigned long ns)
{
    struct __wasi_subscription sub;
    struct __wasi_event ev;
    unsigned n;
    memset(&sub, 0, sizeof sub);
    sub.clock.id = __WASI_CLOCKID_MONOTONIC;
    sub.clock.timeout = ns;
    return __wasi_poll_oneoff(&sub, &ev, 1, &n);
}
