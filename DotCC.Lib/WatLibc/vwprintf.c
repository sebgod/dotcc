/* vwprintf: vfwprintf to stdout. The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>

int vwprintf(const wchar_t *restrict fmt, va_list ap)
{
    return vfwprintf(stdout, fmt, ap);
}
