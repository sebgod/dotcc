/* ldiv: the quotient (truncated toward zero) and remainder of num / den. The wat target's libc (WatLibc), compiled with the program. */
#include <stdlib.h>

ldiv_t ldiv(long num, long den)
{
    ldiv_t r;
    r.quot = num / den;
    r.rem = num % den;
    return r;
}
