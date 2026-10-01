/* wmemcpy: copy n code units; returns dst. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

wchar_t *wmemcpy(wchar_t *dst, wchar_t *src, size_t n)
{
    memcpy(dst, src, n * sizeof(wchar_t));
    return dst;
}
