/* mbsinit: whether a conversion state is the initial one (no character begun), or is NULL. The wat target's libc (WatLibc), compiled with the program. */
#include <mbstate.h>

int mbsinit(const mbstate_t *st)
{
    return !st || !st->__opaque2;
}
