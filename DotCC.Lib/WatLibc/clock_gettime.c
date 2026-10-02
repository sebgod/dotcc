/* clock_gettime: clock clk's time (POSIX), through WASI's clock_time_get, whose clock ids are
   <time.h>'s CLOCK_* (Linux's). The wat target's libc (WatLibc), compiled with the program. */
#include <time.h>
#include <posix_impl.h>

int clock_gettime(clockid_t clk, struct timespec *ts)
{
    unsigned long ns;
    int err = __wasi_clock_time_get((unsigned)clk, 1, &ns);
    if (err)
    {
        return __wasi_fail(err);
    }
    ts->tv_sec = (time_t)(ns / 1000000000);
    ts->tv_nsec = (long)(ns % 1000000000);
    return 0;
}
