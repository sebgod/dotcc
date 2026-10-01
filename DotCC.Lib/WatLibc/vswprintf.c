/* vswprintf: wide formatted output into s, at most n wide characters with the NUL; the count, or
   -1 when it does not fit (what fits is still written, NUL-terminated). The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>
#include <stdlib.h>

int vswprintf(wchar_t *restrict s, size_t n, const wchar_t *restrict fmt, va_list ap)
{
    char *fmt8 = __wcs_to_utf8_dup(fmt);
    if (!fmt8) return -1;
    va_list ap2;
    va_copy(ap2, ap);
    int len = vsnprintf(NULL, 0, fmt8, ap2);
    va_end(ap2);
    char *text = len < 0 ? NULL : malloc((size_t)len + 1);
    int r = -1;
    if (text)
    {
        vsnprintf(text, (size_t)len + 1, fmt8, ap);
        size_t units = __utf8_to_wcs(NULL, text, (size_t)len, (size_t)-1);
        if (n > 0)
        {
            size_t stored = __utf8_to_wcs(s, text, (size_t)len, n - 1);
            s[stored] = 0;
        }
        if (units < n) r = (int)units;
        free(text);
    }
    free(fmt8);
    return r;
}
