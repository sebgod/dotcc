/* wmemmove: copy n code units, the ranges allowed to overlap; returns dst. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

wchar_t *wmemmove(wchar_t *dst, wchar_t *src, size_t n)
{
    memmove(dst, src, n * sizeof(wchar_t));
    return dst;
}
