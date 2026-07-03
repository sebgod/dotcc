/* C23 char8_t (<uchar.h>) + u8"…" / u8'x' literals. dotcc lowers char8_t to C#
   byte, and u8"…" rides the existing narrow UTF-8 string path (dotcc's plain
   string literals are already UTF-8) — near-zero new machinery. */
#include <uchar.h>
#include <stdio.h>

int main(void) {
    printf("sizeof=%d\n", (int)sizeof(char8_t));          /* 1 */

    char8_t ch = u8'A';
    printf("ch=%d\n", (int)ch);                           /* 65 */

    const char8_t *s = u8"Hi";                            /* UTF-8 string → char8_t* */
    printf("s0=%d s1=%d\n", (int)s[0], (int)s[1]);        /* 72 105 */

    char8_t buf[] = u8"AB";                               /* array init from u8"…" */
    int n = 0;
    while (buf[n] != 0) { n++; }
    printf("len=%d %d %d\n", n, (int)buf[0], (int)buf[1]); /* 2 65 66 */

    /* u8 keeps raw UTF-8 bytes: U+00E9 ('é') is the two bytes 0xC3 0xA9. */
    const char8_t *e = u8"\xC3\xA9";
    printf("utf8=%d %d\n", (int)e[0], (int)e[1]);         /* 195 169 */
    return 0;
}
