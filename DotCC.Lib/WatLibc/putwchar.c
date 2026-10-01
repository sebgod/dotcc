/* putwchar: fputwc to stdout. The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>

wint_t putwchar(wchar_t c)
{
    return fputwc(c, stdout);
}
