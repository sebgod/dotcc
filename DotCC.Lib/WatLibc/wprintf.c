/* wprintf: vfwprintf over its arguments. The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>

int wprintf(const wchar_t *restrict fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    int ret = vfwprintf(stdout, fmt, ap);
    va_end(ap);
    return ret;
}
