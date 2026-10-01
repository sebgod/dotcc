/* getwc: fgetwc. The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>

wint_t getwc(FILE *f)
{
    return fgetwc(f);
}
