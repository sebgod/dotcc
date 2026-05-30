/* `char s[] = "…"` — a char array initialized from a string literal (C89).
 * Unlike a bare string literal (a pinned read-only RVA), a char array is a
 * MUTABLE copy, so dotcc decodes the string to bytes and stackalloc's them
 * (+ a NUL, zero-padded to the explicit size). gcc is the oracle. */
#include <stdio.h>

int main(void) {
    char s[]    = "hello";   /* implicit size → 6 (5 + NUL) */
    char buf[10] = "hi";     /* explicit size, zero-padded */
    s[0] = 'H';              /* mutable in place */
    printf("%s %s len=%d\n", s, buf, (int)sizeof(s));
    return 0;
}
