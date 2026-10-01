/* wmemset: set n code units to c; returns dst. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

wchar_t *wmemset(wchar_t *dst, wchar_t c, size_t n)
{
    for (size_t i = 0; i < n; i++) { dst[i] = c; }
    return dst;
}
