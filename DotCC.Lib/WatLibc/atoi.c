/* atoi: strtol in base 10, as an int. The wat target's libc (WatLibc), compiled with the program. */
#include <stdlib.h>

int atoi(const char *nptr)
{
    return (int)strtol(nptr, NULL, 10);
}
