/* vfwprintf: wide formatted output to a stream: the format as UTF-8 through the narrow printf,
   its text written in one go; the count of wide characters it is. The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>
#include <stdlib.h>

int vfwprintf(FILE *restrict f, const wchar_t *restrict fmt, va_list ap)
{
    char *fmt8 = __wcs_to_utf8_dup(fmt);
    if (!fmt8) return -1;
    va_list ap2;
    va_copy(ap2, ap);
    int n = vsnprintf(NULL, 0, fmt8, ap2);
    va_end(ap2);
    char *text = n < 0 ? NULL : malloc((size_t)n + 1);
    int r = -1;
    if (text)
    {
        vsnprintf(text, (size_t)n + 1, fmt8, ap);
        if (fwrite(text, 1, (size_t)n, f) == (size_t)n) r = (int)__utf8_to_wcs(NULL, text, (size_t)n, (size_t)-1);
        free(text);
    }
    free(fmt8);
    return r;
}
