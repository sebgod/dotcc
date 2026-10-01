/* swscanf: vswscanf over its arguments. The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>

int swscanf(const wchar_t *restrict s, const wchar_t *restrict fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    int ret = vswscanf(s, fmt, ap);
    va_end(ap);
    return ret;
}
