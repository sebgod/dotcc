/* swprintf: vswprintf over its arguments. The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>

int swprintf(wchar_t *restrict s, size_t n, const wchar_t *restrict fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    int ret = vswprintf(s, n, fmt, ap);
    va_end(ap);
    return ret;
}
