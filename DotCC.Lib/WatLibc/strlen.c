/* strlen: the bytes before the terminating NUL. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>

int strlen(char *s)
{
    char *p = s;
    while (*p) { p++; }
    return (int)(p - s);
}
