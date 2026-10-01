/* abs: the magnitude of n. The wat target's libc (WatLibc), compiled with the program. */
#include <stdlib.h>

int abs(int n)
{
    return n < 0 ? -n : n;
}
