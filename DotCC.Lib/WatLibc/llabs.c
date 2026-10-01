/* llabs: the magnitude of n. The wat target's libc (WatLibc), compiled with the program. */
#include <stdlib.h>

long llabs(long n)
{
    return n < 0 ? -n : n;
}
