/* C23 digit separators: a single quote between digits groups them for
 * readability (`1'000'000`). They carry no value, so dotcc just strips them
 * (C# uses `_` and doesn't need them); they're accepted in decimal, hex, and
 * binary literals. gcc (-std=c2x) is the oracle. */
#include <stdio.h>

int main(void) {
    int  million = 1'000'000;
    int  hexmask = 0xFF'FF;          /* 65535 */
    int  bits    = 0b1010'0101;      /* 165   */
    long trillion = 1'000'000'000'000L;
    printf("%d %d %d %ld\n", million, hexmask, bits, trillion);
    return 0;
}
