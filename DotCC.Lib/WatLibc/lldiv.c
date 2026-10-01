/* lldiv: the quotient (truncated toward zero) and remainder of num / den. The wat target's libc (WatLibc), compiled with the program. */
#include <stdlib.h>

lldiv_t lldiv(long num, long den)
{
    lldiv_t r;
    r.quot = num / den;
    r.rem = num % den;
    return r;
}
