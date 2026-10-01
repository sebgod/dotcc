/* toupper: an ASCII small letter's capital, anything else unchanged. The wat target's libc (WatLibc), compiled with the program. */
#include <ctype.h>

int toupper(int c)
{
    return c >= 'a' && c <= 'z' ? c - 32 : c;
}
