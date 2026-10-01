/* islower: non-zero for an ASCII small letter (the C locale; any byte past ASCII is 0). The wat target's libc (WatLibc), compiled with the program. */
#include <ctype.h>

int islower(int c)
{
    return c >= 'a' && c <= 'z';
}
