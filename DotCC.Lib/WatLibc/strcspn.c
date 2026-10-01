/* strcspn: the length of s's prefix with none of reject's bytes. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>

int strcspn(char *s, char *reject)
{
    int n = 0;
    for (; s[n]; n++)
    {
        char *r = reject;
        while (*r && *r != s[n]) { r++; }
        if (*r) { break; }
    }
    return n;
}
