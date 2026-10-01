/* tolower: an ASCII capital's small letter, anything else unchanged. The wat target's libc (WatLibc), compiled with the program. */
#include <ctype.h>

int tolower(int c)
{
    return c >= 'A' && c <= 'Z' ? c + 32 : c;
}
