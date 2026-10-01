/* imaxabs: the magnitude of n. The wat target's libc (WatLibc), compiled with the program. */
#include <inttypes.h>

intmax_t imaxabs(intmax_t n)
{
    return n < 0 ? -n : n;
}
