/* strchr: the first c in s (its NUL included), or NULL. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>

char *strchr(char *s, int c)
{
    for (;; s++)
    {
        if (*s == (char)c) { return s; }
        if (*s == 0) { return NULL; }
    }
}
