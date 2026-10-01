/* getwchar: fgetwc from stdin. The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>

wint_t getwchar(void)
{
    return fgetwc(stdin);
}
