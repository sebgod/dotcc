/* wcscspn: the length of the leading run of code units not in reject. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

int wcscspn(wchar_t *s, wchar_t *reject)
{
    int n = 0;
    while (s[n] && !wcschr(reject, s[n])) { n++; }
    return n;
}
