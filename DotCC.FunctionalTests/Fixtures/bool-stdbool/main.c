// `_Bool` (C99 keyword) and `bool`/`true`/`false` (stdbool.h macros).
// MSVC oracle confirms both forms produce identical stdout.

#include "stdio.h"
#include "stdbool.h"

// Function returning bool — exercises return-by-value and parameter type.
bool is_positive(int x) {
    return x > 0;
}

_Bool is_even(int x) {
    return (x % 2) == 0;
}

int main() {
    bool a = true;
    bool b = false;
    printf("a=%d b=%d\n", a, b);

    // Comparison results assign cleanly to bool.
    bool gt = 5 > 3;
    bool eq = 4 == 5;
    printf("gt=%d eq=%d\n", gt, eq);

    // Use in conditional contexts.
    if (is_positive(7)) {
        printf("7 is positive\n");
    }
    if (!is_positive(-3)) {
        printf("-3 is not positive\n");
    }
    if (is_even(4) && !is_even(5)) {
        printf("4 even, 5 odd\n");
    }

    // Logical combinations with bool operands.
    bool both = is_positive(1) && is_positive(2);
    bool either = is_positive(-1) || is_positive(2);
    printf("both=%d either=%d\n", both, either);

    return 0;
}
