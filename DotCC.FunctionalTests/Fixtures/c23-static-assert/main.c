/* _Static_assert (C11) and the C23 lowercase `static_assert` keyword, both
 * file scope and block scope, two-arg and message-less arities. Compile-time
 * only: dotcc EVALUATES the controlling expression (a zero or non-constant
 * one fails the compile), and a passing static_assert produces no output —
 * observably identical to gcc checking it. Requires -std=c23 for the bare
 * lowercase keyword (see std.txt). */
#include <stdio.h>

/* file-scope, C11 two-arg form */
_Static_assert(sizeof(int) >= 2, "int must be at least 16 bits");

/* file-scope over enum constants, ternary, and logical operators — the
 * integer-constant-expression surface the comptime interpreter folds */
enum Dim { WIDTH = 4, HEIGHT = 8 };
_Static_assert(WIDTH < HEIGHT ? 1 : 0, "enum constants fold through a ternary");
_Static_assert(WIDTH * HEIGHT == 32 && sizeof(char) == 1, "arithmetic and logic fold");

int main(void) {
    /* block-scope C23 lowercase keyword, both arities */
    static_assert(1 + 1 == 2, "arithmetic still works");
    static_assert(sizeof(long) >= sizeof(int));
    static_assert((WIDTH | HEIGHT) == 12, "bitwise folds");

    printf("asserts passed at compile time\n");
    return 0;
}
