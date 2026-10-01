/* localtime_r: WASI has no time zone, so local time is UTC: gmtime_r. The wat target's libc
   (WatLibc), compiled with the program. */
#include <time.h>

struct tm *localtime_r(time_t *timer, struct tm *result)
{
    return gmtime_r(timer, result);
}
