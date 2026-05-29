/* _Static_assert (C11) and the C23 lowercase `static_assert` keyword, both
 * file scope and block scope, two-arg and message-less arities. Compile-time
 * only: a passing static_assert produces no output, so dotcc dropping it to a
 * comment is observably identical to gcc actually checking it. Requires
 * -std=c23 for the bare lowercase keyword (see std.txt). */
#include <stdio.h>

/* file-scope, C11 two-arg form */
_Static_assert(sizeof(int) >= 2, "int must be at least 16 bits");

int main(void) {
    /* block-scope C23 lowercase keyword, both arities */
    static_assert(1 + 1 == 2, "arithmetic still works");
    static_assert(sizeof(long) >= sizeof(int));

    printf("asserts passed at compile time\n");
    return 0;
}
