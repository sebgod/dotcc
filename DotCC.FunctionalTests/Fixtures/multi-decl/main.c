// Multi-declarator: `int x, y, z;` and mixed-init `int a = 1, b, c = 3;`.

#include "stdio.h"

int main() {
    // Three names, no init.
    int x, y, z;
    x = 1;
    y = 2;
    z = 3;
    printf("xyz: %d %d %d\n", x, y, z);

    // Mixed: init / no-init / init.
    int a = 10, b, c = 30;
    b = a + c;
    printf("abc: %d %d %d\n", a, b, c);

    // Single-declarator still works (the common case).
    int s = 42;
    printf("s=%d\n", s);

    // Multi-decl of pointers — Type=int*, names p and q both pointers.
    int* p = &a;
    int* q = &c;
    printf("*p=%d *q=%d\n", *p, *q);

    return 0;
}
