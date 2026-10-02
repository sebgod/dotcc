/* clock_getres: clock clk's resolution (POSIX), through WASI's clock_res_get; res may be null.
   The wat target's libc (WatLibc), compiled with the program. */
#include <time.h>
#include <posix_impl.h>

int clock_getres(clockid_t clk, struct timespec *res)
{
    unsigned long ns;
    int err = __wasi_clock_res_get((unsigned)clk, &ns);
    if (err)
    {
        return __wasi_fail(err);
    }
    if (res)
    {
        res->tv_sec = (time_t)(ns / 1000000000);
        res->tv_nsec = (long)(ns % 1000000000);
    }
    return 0;
}
