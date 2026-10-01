/* strcat: appends src at dst's NUL. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>

char *strcat(char *dst, char *src)
{
    char *d = dst;
    while (*d) { d++; }
    while ((*d++ = *src++) != 0) { }
    return dst;
}
