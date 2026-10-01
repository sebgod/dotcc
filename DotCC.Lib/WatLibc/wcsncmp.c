/* wcsncmp: wcscmp over at most n code units. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

int wcsncmp(wchar_t *a, wchar_t *b, size_t n)
{
    for (size_t i = 0; i < n; i++)
    {
        if (a[i] != b[i]) { return (int)a[i] - (int)b[i]; }
        if (a[i] == 0) { return 0; }
    }
    return 0;
}
