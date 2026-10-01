/* wcslen: the code units before the terminating 0. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

int wcslen(wchar_t *s)
{
    int n = 0;
    while (s[n]) { n++; }
    return n;
}
