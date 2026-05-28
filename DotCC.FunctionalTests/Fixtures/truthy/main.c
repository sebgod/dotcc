// Exercises C-truthy conditional contexts. In real C, the conditions of
// if/while/for are "truthy" — non-zero ints, non-null pointers, and the
// usual bools all count as true. dotcc wraps each cond in a B() overloaded
// helper so all three lower cleanly to C# bool.

#include "stdio.h"
#include "stdlib.h"

int main() {
    // int-truthy: countdown by decrement-as-condition.
    int n = 5;
    while (n) {
        printf("n=%d\n", n);
        n = n - 1;
    }

    // pointer-truthy: if (p) is true when p is non-null.
    int* p = (int*)malloc(4);
    if (p) {
        *p = 42;
        printf("alloc ok: *p=%d\n", *p);
        free(p);
    }

    // for-loop with int cond.
    for (int i = 3; i; i--) {
        printf("count=%d\n", i);
    }

    return 0;
}
