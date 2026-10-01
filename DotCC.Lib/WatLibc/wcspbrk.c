/* wcspbrk: the first code unit of s in accept, or NULL. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

wchar_t *wcspbrk(wchar_t *s, wchar_t *accept)
{
    s += wcscspn(s, accept);
    return *s ? s : NULL;
}
