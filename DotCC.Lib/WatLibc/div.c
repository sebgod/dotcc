/* div: the quotient (truncated toward zero) and remainder of num / den. The wat target's libc (WatLibc), compiled with the program. */
#include <stdlib.h>

div_t div(int num, int den)
{
    div_t r;
    r.quot = num / den;
    r.rem = num % den;
    return r;
}
