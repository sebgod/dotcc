/* An integer constant EXPRESSION stored into a narrower type truncates like any
 * other narrowing store (C11 6.3.1.3p2, modulo 2^width for an unsigned target).
 * C# checks constant conversions at compile time (CS0221), so dotcc wraps the
 * inserted cast in `unchecked` unless it can prove the value fits. A lone
 * literal was already range-checked; an operator over constants
 * (`250u + 10u`, `0xFFFFu * 3u`) had been cast bare and failed to build. */
#include <stdio.h>

int main(void) {
    unsigned char a = 7u;                   /* fits: no wrapper needed */
    unsigned char b = 250u + 10u;           /* 260 truncates to 4 */
    unsigned short s = 0xFFFFu * 3u;        /* 0x2FFFD truncates to 0xFFFD = 65533 */
    signed char c = -3;                     /* fits */
    unsigned char arr[3] = { 1u, 300u, 7 }; /* an initializer element: 300 truncates to 44 */
    printf("%d %d %d %d %d\n", a, b, s, c, arr[1]);
    return 0;
}
