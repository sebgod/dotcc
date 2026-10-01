/* wmemcmp: the difference of the first differing of n code units, 0 when equal. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

int wmemcmp(wchar_t *a, wchar_t *b, size_t n)
{
    for (size_t i = 0; i < n; i++)
    {
        if (a[i] != b[i]) { return (int)a[i] - (int)b[i]; }
    }
    return 0;
}
