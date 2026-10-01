/* ctime_r: asctime_r of localtime_r. The wat target's libc (WatLibc), compiled with the program. */
#include <time.h>
#include <stddef.h>

char *ctime_r(time_t *timer, char *buf)
{
    struct tm tmp;
    if (localtime_r(timer, &tmp) == NULL) { return NULL; }
    return asctime_r(&tmp, buf);
}
