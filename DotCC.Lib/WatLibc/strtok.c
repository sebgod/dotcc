/* strtok: the next token of str (or, given NULL, of the string the last call was given) that
   delim's bytes separate; NULL when there is none left. The wat target's libc (WatLibc),
   compiled with the program. */
#include <string.h>

char *strtok(char *str, char *delim)
{
    static char *next;
    if (str) { next = str; }
    if (next == NULL) { return NULL; }
    next += strspn(next, delim);
    if (*next == 0)
    {
        next = NULL;
        return NULL;
    }
    char *token = next;
    next += strcspn(next, delim);
    if (*next) { *next++ = 0; }
    else { next = NULL; }
    return token;
}
