/* strrchr: the last c in s (its NUL included), or NULL. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>

char *strrchr(char *s, int c)
{
    char *last = NULL;
    for (;; s++)
    {
        if (*s == (char)c) { last = s; }
        if (*s == 0) { return last; }
    }
}
