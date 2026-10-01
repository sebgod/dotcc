/* __wcs_to_utf8_dup: a wide string as a new UTF-8 string. The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>
#include <stdlib.h>

char *__wcs_to_utf8_dup(const wchar_t *src)
{
    size_t n = __wcs_to_utf8(NULL, src, (size_t)-1);
    char *s = malloc(n + 1);
    if (!s) return NULL;
    __wcs_to_utf8(s, src, n);
    s[n] = 0;
    return s;
}
