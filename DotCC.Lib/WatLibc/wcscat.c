/* wcscat: append src to dst; returns dst. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

wchar_t *wcscat(wchar_t *dst, wchar_t *src)
{
    wcscpy(dst + wcslen(dst), src);
    return dst;
}
