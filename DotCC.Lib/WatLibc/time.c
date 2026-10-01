/* time: the seconds since the Epoch, from WASI's realtime clock, or -1 when the host has none.
   The wat target's libc (WatLibc), compiled with the program. */
#include <time.h>
#include <wasi.h>

time_t time(time_t *t)
{
    unsigned long ns;
    time_t now = __wasi_clock_time_get(__WASI_CLOCKID_REALTIME, 1000000000, &ns) == 0
        ? (time_t)(ns / 1000000000)
        : (time_t)-1;
    if (t) { *t = now; }
    return now;
}
