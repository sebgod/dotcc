/* wcschr: the first c in s (the terminating 0 included), or NULL. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

wchar_t *wcschr(wchar_t *s, wchar_t c)
{
    for (;; s++)
    {
        if (*s == c) { return s; }
        if (*s == 0) { return NULL; }
    }
}
