/* wcstof: a float from the wide string, rounded once (strtof). Like the C# runtime's, the leading ASCII run goes through the narrow parser and endptr comes back to the wide string. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

/* The leading ASCII run of nptr as a narrow string (a number is all ASCII), in buf when it
   fits, else in a fresh allocation. */
static char *ascii_run(wchar_t *nptr, char *buf, int cap)
{
    int n = 0;
    while (nptr[n] && nptr[n] <= 0x7F) { n++; }
    char *out = n < cap ? buf : malloc((size_t)n + 1);
    for (int i = 0; i < n; i++) { out[i] = (char)nptr[i]; }
    out[n] = 0;
    return out;
}

float wcstof(wchar_t *nptr, wchar_t **endptr)
{
    char buf[128];
    char *s = ascii_run(nptr, buf, (int)sizeof buf);
    char *end;
    float v = strtof(s, &end);
    if (endptr) { *endptr = nptr + (end - s); }
    if (s != buf) { free(s); }
    return v;
}
