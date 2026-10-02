/* wcstombs: the wide string src as UTF-8 (the multibyte encoding) into dst, at most n bytes and
   never part of a character, with a NUL when it fits (C11 7.22.8.2); the bytes written, the NUL
   not counted. With dst null, the bytes the whole string takes. The wat target's libc (WatLibc), compiled with the program. */
#include <stdlib.h>
#include <wide_impl.h>

size_t wcstombs(char *dst, const wchar_t *src, size_t n)
{
    size_t k;
    if (!dst)
    {
        return __wcs_to_utf8(0, src, (size_t)-1);
    }
    k = __wcs_to_utf8(dst, src, n);
    if (k < n)
    {
        dst[k] = 0;
    }
    return k;
}
