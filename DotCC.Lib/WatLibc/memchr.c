/* memchr: the first byte equal to c in count bytes, or NULL. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>

void *memchr(void *s, int c, int count)
{
    unsigned char *p = s;
    for (int i = 0; i < count; i++)
    {
        if (p[i] == (unsigned char)c) { return p + i; }
    }
    return NULL;
}
