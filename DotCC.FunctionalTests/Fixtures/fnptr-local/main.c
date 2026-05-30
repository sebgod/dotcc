/* Function-pointer LOCAL declarator `int (*op)(int, int)` — the raw (non-typedef)
 * form. dotcc lowers it to a C# `delegate*<int, int, int>`. A bare function-name
 * initializer (`= add`, which C decays to a pointer) is given the `&` C# needs;
 * an explicit `&sub` passes through. Unnamed parameters in the pointed-to type
 * (`(int, int)`) are now supported. gcc is the oracle. */
#include <stdio.h>

int add(int a, int b) { return a + b; }
int sub(int a, int b) { return a - b; }

int main(void) {
    int (*op)(int, int) = add;    /* bare name → auto-& */
    printf("add: %d\n", op(10, 3));
    op = &sub;                     /* explicit address-of */
    printf("sub: %d\n", op(10, 3));
    return 0;
}
