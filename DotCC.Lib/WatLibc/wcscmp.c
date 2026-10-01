/* wcscmp: the difference of the first differing code units, 0 when equal. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

int wcscmp(wchar_t *a, wchar_t *b)
{
    while (*a && *a == *b) { a++; b++; }
    return (int)*a - (int)*b;
}
