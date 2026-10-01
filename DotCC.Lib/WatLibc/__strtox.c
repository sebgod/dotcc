/* __strtox: the strto* integer family's parser. Skips white space, reads a sign and the base's prefix (0x for 16; 0 for 8 when base is 0), then the digits, as an unsigned magnitude that wraps like unsigned long; *endptr is past the last digit, or nptr when there is none. The wat target's libc (WatLibc), compiled with the program. */
#include <stddef.h>

unsigned long __strtox(const char *nptr, char **endptr, int base, int *negative)
{
    const char *s = nptr;
    while (*s == ' ' || (*s >= '\t' && *s <= '\r')) { s++; }
    *negative = 0;
    if (*s == '-' || *s == '+') { *negative = *s == '-'; s++; }
    if ((base == 0 || base == 16) && s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
    {
        s += 2;
        base = 16;
    }
    else if (base == 0)
    {
        base = s[0] == '0' ? 8 : 10;
    }
    unsigned long value = 0;
    const char *digits = s;
    for (;; s++)
    {
        int d;
        if (*s >= '0' && *s <= '9') { d = *s - '0'; }
        else if (*s >= 'a' && *s <= 'z') { d = *s - 'a' + 10; }
        else if (*s >= 'A' && *s <= 'Z') { d = *s - 'A' + 10; }
        else { break; }
        if (d >= base) { break; }
        value = value * (unsigned long)base + (unsigned long)d;
    }
    if (endptr) { *endptr = (char *)(s == digits ? nptr : s); }
    return value;
}
