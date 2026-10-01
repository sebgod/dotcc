/* wmemchr: the first c among n code units, or NULL. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

wchar_t *wmemchr(wchar_t *s, wchar_t c, size_t n)
{
    for (size_t i = 0; i < n; i++)
    {
        if (s[i] == c) { return s + i; }
    }
    return NULL;
}
