/* Function-pointer PARAMETER `int (*op)(int, int)` — the raw (non-typedef)
 * callback form. dotcc lowers it to a `delegate*<int, int, int>` parameter, and
 * a bare function-name argument (`apply(add, …)`) decays to `&add` as C
 * requires. The pointed-to type's own params are kept off the enclosing
 * function's signature. gcc is the oracle. */
#include <stdio.h>

int add(int a, int b) { return a + b; }
int mul(int a, int b) { return a * b; }

int apply(int (*op)(int, int), int x, int y) { return op(x, y); }

int main(void) {
    printf("%d %d\n", apply(add, 6, 7), apply(mul, 6, 7));
    return 0;
}
