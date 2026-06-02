/* C allows an implicit narrowing integer conversion at a store (init /
 * assignment / return) — a wider value silently truncates to the narrower type.
 * C# requires an explicit cast there, so dotcc inserts it; the truncation must
 * match C exactly. (-Wconversion, opt-in, additionally warns at each such site;
 * the warning goes to stderr so it isn't part of this stdout snapshot.) */
#include <stdio.h>
typedef unsigned char u8;

/* return narrowing: int -> u8 */
static u8 trunc8(int x) { return x; }

int main(void) {
    int big = 0x1234;          /* 4660 */
    u8  a = big;               /* decl-init narrowing: 0x1234 & 0xFF = 0x34 = 52 */
    u8  b;
    b = big + 1;               /* assignment narrowing: 0x1235 & 0xFF = 53 */
    u8  c = trunc8(0x1FF);     /* return narrowing inside trunc8: 0x1FF & 0xFF = 255 */
    short s = 0x12345;         /* out-of-range constant narrowing: 0x2345 = 9029 */

    printf("%d %d %d %d\n", (int)a, (int)b, (int)c, (int)s);  /* 52 53 255 9029 */
    return 0;
}
