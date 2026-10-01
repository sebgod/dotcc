/* strncat: appends at most n bytes of src, then a NUL. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>

char *strncat(char *dst, char *src, int n)
{
    char *d = dst;
    while (*d) { d++; }
    for (; n > 0 && *src; n--) { *d++ = *src++; }
    *d = 0;
    return dst;
}
