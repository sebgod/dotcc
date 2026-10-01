/* difftime: end - beginning, in seconds. The wat target's libc (WatLibc), compiled with the program. */
#include <time.h>

double difftime(time_t end, time_t beginning)
{
    return (double)(end - beginning);
}
