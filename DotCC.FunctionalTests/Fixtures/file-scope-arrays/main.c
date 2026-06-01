#include <stdio.h>

/* File-scope arrays in their several forms. Each lowers to a `T*` field in
 * DotCcGlobals backed by a pinned, rooted managed array (Libc.GlobalArray*) —
 * the same `T*` shape a block-scope stackalloc array gets, so subscripting and
 * the sizeof-length idiom work identically. */

extern const int fib[];                  /* extern declaration ... */
const int fib[] = { 1, 1, 2, 3, 5, 8 };  /* ... and its definition (implicit size) */

int scratch[8];                          /* zeroed (C zero-inits static storage) */
int grid[2][3] = { {1,2,3}, {4,5,6} };   /* multi-dimensional, nested initializer */
static const char tag[] = "dotcc";       /* char array from a string literal */
static int squares[6] = { 0, 1, 4, 9 };  /* sized, partial init (tail zero-filled) */

int main(void) {
    scratch[5] = 99;
    printf("fib5=%d n=%d\n", fib[5], (int)(sizeof(fib) / sizeof(fib[0])));
    printf("scratch=%d grid=%d\n", scratch[5], grid[1][2]);
    printf("tag=%s sq2=%d sq4=%d\n", tag, squares[2], squares[4]);
    return 0;
}
