/* putwc: fputwc. The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>

wint_t putwc(wchar_t c, FILE *f)
{
    return fputwc(c, f);
}
