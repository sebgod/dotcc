/* wcsstr: the first occurrence of needle in haystack, haystack for an empty needle, or NULL. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

wchar_t *wcsstr(wchar_t *haystack, wchar_t *needle)
{
    if (*needle == 0) { return haystack; }
    for (; *haystack; haystack++)
    {
        int i = 0;
        while (needle[i] && haystack[i] == needle[i]) { i++; }
        if (needle[i] == 0) { return haystack; }
    }
    return NULL;
}
