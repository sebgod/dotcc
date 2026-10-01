/* timespec_get: the TIME_UTC time, from WASI's realtime clock; base on success, 0 otherwise. The wat target's libc (WatLibc), compiled with the program. */
#include <time.h>
#include <wasi.h>

int timespec_get(struct timespec *ts, int base)
{
    unsigned long ns;
    if (base != TIME_UTC || __wasi_clock_time_get(__WASI_CLOCKID_REALTIME, 1, &ns) != 0) { return 0; }
    ts->tv_sec = (time_t)(ns / 1000000000);
    ts->tv_nsec = (long)(ns % 1000000000);
    return base;
}
