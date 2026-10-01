/* strcmp: compares as unsigned char, up to the first difference or NUL. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>

int strcmp(char *a, char *b)
{
    while (*a && *a == *b) { a++; b++; }
    return (unsigned char)*a - (unsigned char)*b;
}
