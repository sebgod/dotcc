/* strpbrk: the first byte of s that is in accept, or NULL. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>

char *strpbrk(char *s, char *accept)
{
    for (; *s; s++)
    {
        for (char *a = accept; *a; a++)
        {
            if (*a == *s) { return s; }
        }
    }
    return NULL;
}
