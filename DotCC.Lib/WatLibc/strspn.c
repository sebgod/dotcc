/* strspn: the length of s's prefix made of accept's bytes. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>

int strspn(char *s, char *accept)
{
    int n = 0;
    for (; s[n]; n++)
    {
        char *a = accept;
        while (*a && *a != s[n]) { a++; }
        if (*a == 0) { break; }
    }
    return n;
}
