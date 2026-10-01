/* strstr: the first occurrence of needle in haystack, or NULL. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>

char *strstr(char *haystack, char *needle)
{
    if (*needle == 0) { return haystack; }
    for (; *haystack; haystack++)
    {
        char *h = haystack;
        char *n = needle;
        while (*h && *n && *h == *n) { h++; n++; }
        if (*n == 0) { return haystack; }
    }
    return NULL;
}
