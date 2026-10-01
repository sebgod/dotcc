/* strncpy: copies at most n bytes of src, padding with NULs to n. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>

char *strncpy(char *dst, char *src, int n)
{
    int i = 0;
    for (; i < n && src[i]; i++) { dst[i] = src[i]; }
    for (; i < n; i++) { dst[i] = 0; }
    return dst;
}
