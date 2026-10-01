/* asctime: asctime_r into a buffer the next call reuses. The wat target's libc (WatLibc),
   compiled with the program. */
#include <time.h>

char *asctime(struct tm *t)
{
    static char buf[64];
    return asctime_r(t, buf);
}
