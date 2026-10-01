/* atoll: strtoll in base 10. The wat target's libc (WatLibc), compiled with the program. */
#include <stdlib.h>

long atoll(const char *nptr)
{
    return strtoll(nptr, NULL, 10);
}
