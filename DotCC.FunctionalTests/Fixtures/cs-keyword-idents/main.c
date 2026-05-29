/* C identifiers that are C# reserved keywords (new, lock, object, ref, string)
 * are valid C names but illegal as bare C# identifiers — dotcc @-escapes them
 * on emit. This exercises the must-match decl/reference pairs: keyword local
 * vars, a keyword function name + keyword parameter, a keyword call, and
 * keyword struct fields read through member access. (The struct *tag* is a
 * non-keyword — type-name escaping is a separate, deferred gap.) gcc accepts
 * all of these verbatim, so the oracle confirms the @-escaped C# matches. */
#include <stdio.h>

struct rec {
    int new;    /* C# keyword field */
    int lock;   /* C# keyword field */
};

int object(int ref) {       /* C# keyword function name + parameter */
    return ref * 2;
}

int main(void) {
    int new = 10;               /* keyword local */
    int string = object(new);   /* keyword local + keyword call + keyword arg */

    struct rec ev;
    ev.new = new;               /* keyword member write */
    ev.lock = string;

    printf("%d %d %d\n", new, string, ev.new + ev.lock);
    return 0;
}
