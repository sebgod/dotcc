/* isupper: non-zero for an ASCII capital (the C locale; any byte past ASCII is 0). The wat target's libc (WatLibc), compiled with the program. */
#include <ctype.h>

int isupper(int c)
{
    return c >= 'A' && c <= 'Z';
}
