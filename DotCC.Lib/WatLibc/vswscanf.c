/* vswscanf: wide formatted input from a wide string: the string and the format as UTF-8 through
   the narrow sscanf. The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>
#include <scanf_impl.h>
#include <stdlib.h>

int vswscanf(const wchar_t *restrict s, const wchar_t *restrict fmt, va_list ap)
{
    char *s8 = __wcs_to_utf8_dup(s);
    char *fmt8 = __wcs_to_utf8_dup(fmt);
    int r = s8 && fmt8 ? vsscanf(s8, fmt8, ap) : -1;
    free(s8);
    free(fmt8);
    return r;
}
