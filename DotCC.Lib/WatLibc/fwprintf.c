/* fwprintf: vfwprintf over its arguments. The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>

int fwprintf(FILE *restrict f, const wchar_t *restrict fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    int ret = vfwprintf(f, fmt, ap);
    va_end(ap);
    return ret;
}
