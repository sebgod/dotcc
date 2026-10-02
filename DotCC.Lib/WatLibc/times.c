/* times: the program's times in clock ticks, sysconf(_SC_CLK_TCK) (100) a second (POSIX): the
   CPU time WASI's process clock reports as the user time, no system time and no children's;
   the result counts ticks on the monotonic clock. The wat target's libc (WatLibc), compiled with the program. */
#include <sys/times.h>
#include <posix_impl.h>

#define TICK_NS 10000000UL

clock_t times(struct tms *buf)
{
    unsigned long now;
    unsigned long cpu = 0;
    int err = __wasi_clock_time_get(__WASI_CLOCKID_MONOTONIC, 1000, &now);
    if (err)
    {
        return __wasi_fail(err);
    }
    if (buf)
    {
        __wasi_clock_time_get(__WASI_CLOCKID_PROCESS_CPUTIME_ID, 1000, &cpu);
        buf->tms_utime = (clock_t)(cpu / TICK_NS);
        buf->tms_stime = 0;
        buf->tms_cutime = 0;
        buf->tms_cstime = 0;
    }
    return (clock_t)(now / TICK_NS);
}
