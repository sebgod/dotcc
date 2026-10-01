/* isspace: non-zero for space, \t, \n, \v, \f or \r (the C locale; any byte past ASCII is 0). The wat target's libc (WatLibc), compiled with the program. */
#include <ctype.h>

int isspace(int c)
{
    return c == ' ' || (c >= '\t' && c <= '\r');
}
