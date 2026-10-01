/* wcstok: the next token of str (or, for NULL, of *saveptr) between delimiters, writing a 0 over the one that ends it; NULL when none is left. The wat target's libc (WatLibc), compiled with the program. */
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

wchar_t *wcstok(wchar_t *str, wchar_t *delim, wchar_t **saveptr)
{
    wchar_t *s = str ? str : *saveptr;
    if (s == NULL) { return NULL; }
    s += wcsspn(s, delim);
    if (*s == 0) { *saveptr = s; return NULL; }
    wchar_t *token = s;
    s += wcscspn(s, delim);
    if (*s) { *s++ = 0; }
    *saveptr = s;
    return token;
}
