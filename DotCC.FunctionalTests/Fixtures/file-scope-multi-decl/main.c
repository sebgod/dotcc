// Exercises the C99 init-declarator-list at file scope:
//   int a, b;                   — multiple declarators, none initialized
//   int x = 10, y = 20;         — multiple declarators, all initialized
//   int p, q = 5, r;            — mixed init / no-init
// All forms compile to one `public static unsafe int <name> [= <expr>];`
// per declarator inside DotCcGlobals.

#include "stdio.h"

int a, b;
int x = 10, y = 20;
int p, q = 5, r;

void set_them() {
    a = 1;
    b = 2;
    p = 100;
    r = 200;
}

int main() {
    set_them();
    printf("a=%d b=%d\n", a, b);
    printf("x=%d y=%d\n", x, y);
    printf("p=%d q=%d r=%d\n", p, q, r);
    return 0;
}
