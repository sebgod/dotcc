/* C string/char escapes + adjacent string-literal concatenation. dotcc decodes
 * each C escape to its byte value: char escapes lower to (byte)N; string
 * escapes re-emit as greedy-safe \xHH in a C# u8 literal (C# has no octal
 * escape, and its \x is greedy — so dotcc decodes per-segment and \-delimits).
 * Adjacent literals "a" "b" concatenate (C phase 6). gcc is the oracle. */
#include <stdio.h>

int main(void) {
    char tab = '\t';        /* 9  */
    char A   = '\x41';      /* 65 — hex char escape */
    char esc = '\033';      /* 27 — octal char escape */
    printf("tab=%d A=%d esc=%d\n", tab, A, esc);

    char *greeting = "Hello, " "world" "!";        /* adjacent concatenation */
    printf("%s\n", greeting);

    char *digits = "\063\064\065";   /* octal string escapes -> "345" */
    char *hi     = "\x48\x69";       /* hex string escapes   -> "Hi"  */
    printf("%s %s\n", digits, hi);

    printf("%d"  " + "  "%d"  " = %d\n", 2, 3, 2 + 3);   /* split format */
    return 0;
}
