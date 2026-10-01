/* memcmp: compares count bytes as unsigned char. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>

int memcmp(void *a, void *b, int count)
{
    unsigned char *p = a;
    unsigned char *q = b;
    for (int i = 0; i < count; i++)
    {
        if (p[i] != q[i]) { return p[i] - q[i]; }
    }
    return 0;
}
