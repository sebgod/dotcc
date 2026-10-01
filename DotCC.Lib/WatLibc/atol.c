/* atol: strtol in base 10. The wat target's libc (WatLibc), compiled with the program. */
#include <stdlib.h>

long atol(const char *nptr)
{
    return strtol(nptr, NULL, 10);
}
