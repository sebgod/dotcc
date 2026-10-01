/* strncmp: strcmp over at most n bytes. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>

int strncmp(char *a, char *b, size_t n)
{
    for (; n > 0; n--, a++, b++)
    {
        if (*a != *b) { return (unsigned char)*a - (unsigned char)*b; }
        if (*a == 0) { return 0; }
    }
    return 0;
}
