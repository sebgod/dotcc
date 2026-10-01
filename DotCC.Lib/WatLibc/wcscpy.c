/* wcscpy: copy src and its terminating 0 into dst; returns dst. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

wchar_t *wcscpy(wchar_t *dst, wchar_t *src)
{
    wchar_t *p = dst;
    while ((*p++ = *src++) != 0) { }
    return dst;
}
