// Function-like #define exercise. MSVC oracle confirms each shape produces
// identical stdout — proves the dotcc expander's substitution matches the
// real C preprocessor at the observable level.

#include "stdio.h"

#define MIN(a, b) ((a) < (b) ? (a) : (b))
#define MAX(a, b) ((a) > (b) ? (a) : (b))
#define SQUARE(x) ((x) * (x))
#define CLAMP(x, lo, hi) MAX(lo, MIN(x, hi))
#define IS_EVEN(n) (((n) % 2) == 0)

// Mix object-like and function-like in the same translation unit.
#define LIMIT 100

int main() {
    // Simple two-arg.
    printf("min(3, 7) = %d\n", MIN(3, 7));
    printf("max(3, 7) = %d\n", MAX(3, 7));

    // Argument is an expression — paren-balanced collection prevents
    // splitting on the `+`'s comma-less operands. (Also exercises that
    // (1+2) is correctly substituted into each of the three `x` positions.)
    printf("square(1 + 2) = %d\n", SQUARE(1 + 2));

    // Three-arg macro.
    printf("clamp(120, 0, %d) = %d\n", LIMIT, CLAMP(120, 0, LIMIT));
    printf("clamp(-5, 0, %d) = %d\n", LIMIT, CLAMP(-5, 0, LIMIT));
    printf("clamp(50, 0, %d) = %d\n", LIMIT, CLAMP(50, 0, LIMIT));

    // Boolean-shaped macro used in a conditional.
    int n = 8;
    if (IS_EVEN(n)) {
        printf("%d is even\n", n);
    }
    n = 7;
    if (IS_EVEN(n)) {
        printf("%d is even\n", n);
    } else {
        printf("%d is odd\n", n);
    }

    return 0;
}
