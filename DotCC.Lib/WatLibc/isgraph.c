/* isgraph: non-zero for printable, space excluded (the C locale; any byte past ASCII is 0). The wat target's libc (WatLibc), compiled with the program. */
#include <ctype.h>

int isgraph(int c)
{
    return c > 32 && c < 127;
}
