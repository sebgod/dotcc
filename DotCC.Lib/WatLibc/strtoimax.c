/* strtoimax: an intmax_t from the string's digits in the given base (2 to 36, or 0 to read the prefix). The wat target's libc (WatLibc), compiled with the program. */
#include <inttypes.h>

unsigned long __strtox(const char *nptr, char **endptr, int base, int *negative);

intmax_t strtoimax(const char *nptr, char **endptr, int base)
{
    int negative;
    unsigned long v = __strtox(nptr, endptr, base, &negative);
    return negative ? -(intmax_t)v : (intmax_t)v;
}
