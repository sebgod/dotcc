/* strcoll: compares by the collation of the locale, which is always "C" here, where it is the byte order strcmp compares by. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>

int strcoll(char *a, char *b)
{
    return strcmp(a, b);
}
