/* C23 predefined keyword constants: `true`, `false`, `nullptr` — first-class
 * with no <stdbool.h> / <stddef.h> include. Requires -std=c23 (see std.txt);
 * the bare `bool` type keyword comes along for the ride. */
#include <stdio.h>

int main(void) {
    bool flag = true;
    bool off = false;
    int* p = nullptr;

    /* Use the booleans in conditional position (where the C->C# `bool`
     * lowering is exact) rather than as printf args. */
    if (flag) {
        printf("flag true\n");
    }
    if (!off) {
        printf("off is false\n");
    }
    if (p == nullptr) {
        printf("p is null\n");
    }
    return 0;
}
