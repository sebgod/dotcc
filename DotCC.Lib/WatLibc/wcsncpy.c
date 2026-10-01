/* wcsncpy: copy at most n code units of src, padding with 0 to n; returns dst. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

wchar_t *wcsncpy(wchar_t *dst, wchar_t *src, size_t n)
{
    size_t i = 0;
    for (; i < n && src[i]; i++) { dst[i] = src[i]; }
    for (; i < n; i++) { dst[i] = 0; }
    return dst;
}
