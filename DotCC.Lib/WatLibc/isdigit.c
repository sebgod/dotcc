/* isdigit: non-zero for an ASCII digit (the C locale; any byte past ASCII is 0). The wat target's libc (WatLibc), compiled with the program. */
#include <ctype.h>

int isdigit(int c)
{
    return c >= '0' && c <= '9';
}
