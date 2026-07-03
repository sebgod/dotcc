/* C11 char32_t (<uchar.h>) + U"…" / U'x' literals — dotcc lowers char32_t to
   a 32-bit unsigned code unit (C# uint). Exercises the type, both literal forms,
   pointer + array init, sizeof, and the char32_t payoff: an astral scalar is ONE
   UTF-32 code unit (a char16_t u"…" would spend two surrogates on it). */
#include <uchar.h>
#include <stdio.h>

int main(void) {
    printf("sizeof=%d\n", (int)sizeof(char32_t));            /* 4 */

    char32_t ch = U'A';
    printf("ch=%u\n", ch);                                   /* 65 */

    const char32_t *s = U"Hi";                               /* string → char32_t* */
    printf("s0=%u s1=%u\n", s[0], s[1]);                     /* 72 105 */

    char32_t buf[] = U"AB";                                  /* array init from U"…" */
    int n = 0;
    while (buf[n] != 0) { n++; }
    printf("len=%d %u %u\n", n, buf[0], buf[1]);             /* 2 65 66 */

    /* An astral Unicode scalar (U+1F600) via a hex escape: ONE char32_t code
       unit — unlike the two UTF-16 surrogates a char16_t string would need. */
    char32_t emoji[] = U"\x1F600Z";
    int m = 0;
    while (emoji[m] != 0) { m++; }
    printf("emoji_len=%d first=%u second=%u\n", m, emoji[0], emoji[1]); /* 2 128512 90 */

    return 0;
}
