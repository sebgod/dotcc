/* C `_Bool` is an INTEGER type: storing any scalar normalizes to 0/1, and a
 * bool converts freely to int in arithmetic, assignment, return, and %d. dotcc
 * lowers `_Bool` to the integer-typed Libc.CBool so all of this works (it used
 * to map to C# `bool` and these forms wouldn't compile). gcc confirms the
 * values. */
#include <stdbool.h>
#include <stdio.h>

int as_int(bool b) {   /* bool parameter, used as int */
    return b + 100;    /* bool -> int arithmetic: 1 + 100 or 0 + 100 */
}

int main(void) {
    bool a = true;     /* 1 */
    bool b = 5;        /* nonzero scalar normalizes to 1 */
    bool c = 0;        /* 0 */
    int sum = a + b + c;          /* bool arithmetic: 1 + 1 + 0 = 2 */
    int x = a;                    /* bool -> int: 1 */
    printf("%d %d %d %d %d %d\n", a, b, c, sum, x, as_int(c));
    return 0;
}
