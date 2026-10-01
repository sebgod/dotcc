/* localtime: localtime_r into a buffer the next call reuses. The wat target's libc (WatLibc),
   compiled with the program. */
#include <time.h>

struct tm *localtime(time_t *timer)
{
    static struct tm buf;
    return localtime_r(timer, &buf);
}
