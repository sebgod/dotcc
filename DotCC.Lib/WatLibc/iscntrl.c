/* iscntrl: non-zero for a control character (the C locale; any byte past ASCII is 0). The wat target's libc (WatLibc), compiled with the program. */
#include <ctype.h>

int iscntrl(int c)
{
    return (c >= 0 && c < 32) || c == 127;
}
