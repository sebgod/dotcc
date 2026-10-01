/* ctime: asctime of localtime, in the buffers those reuse. The wat target's libc (WatLibc),
   compiled with the program. */
#include <time.h>

char *ctime(time_t *timer)
{
    return asctime(localtime(timer));
}
