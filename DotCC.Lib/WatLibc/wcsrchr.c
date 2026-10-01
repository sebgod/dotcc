/* wcsrchr: the last c in s (the terminating 0 included), or NULL. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

wchar_t *wcsrchr(wchar_t *s, wchar_t c)
{
    wchar_t *last = NULL;
    for (;; s++)
    {
        if (*s == c) { last = s; }
        if (*s == 0) { return last; }
    }
}
