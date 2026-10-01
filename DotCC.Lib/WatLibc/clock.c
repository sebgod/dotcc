/* clock: the processor time the program has used, in CLOCKS_PER_SEC units, from WASI's
   process CPU-time clock, or -1 when the host has none. The wat target's libc (WatLibc),
   compiled with the program. */
#include <time.h>
#include <wasi.h>

clock_t clock(void)
{
    unsigned long ns;
    if (__wasi_clock_time_get(__WASI_CLOCKID_PROCESS_CPUTIME_ID, 1000, &ns) != 0) { return (clock_t)-1; }
    return (clock_t)(ns / (1000000000 / CLOCKS_PER_SEC));
}
