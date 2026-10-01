/* wcscoll: in the C locale, collation is code-unit order: wcscmp. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

int wcscoll(wchar_t *a, wchar_t *b)
{
    return wcscmp(a, b);
}
