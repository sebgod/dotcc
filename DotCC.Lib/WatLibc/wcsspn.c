/* wcsspn: the length of the leading run of code units in accept. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

int wcsspn(wchar_t *s, wchar_t *accept)
{
    int n = 0;
    while (s[n] && wcschr(accept, s[n])) { n++; }
    return n;
}
