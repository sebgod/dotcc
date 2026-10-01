/* wcsncat: append at most n code units of src to dst, then a 0; returns dst. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

wchar_t *wcsncat(wchar_t *dst, wchar_t *src, size_t n)
{
    wchar_t *p = dst + wcslen(dst);
    size_t i = 0;
    for (; i < n && src[i]; i++) { p[i] = src[i]; }
    p[i] = 0;
    return dst;
}
