/* ispunct: non-zero for printable, neither a letter, a digit nor space (the C locale; any byte past ASCII is 0). The wat target's libc (WatLibc), compiled with the program. */
#include <ctype.h>

int ispunct(int c)
{
    return c > 32 && c < 127 && !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'));
}
