/* strcpy: copies src, its NUL included. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>

char *strcpy(char *dst, char *src)
{
    char *d = dst;
    while ((*d++ = *src++) != 0) { }
    return dst;
}
