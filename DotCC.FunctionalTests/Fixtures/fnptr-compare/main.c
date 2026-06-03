// Comparing a function pointer to a bare function name: `fp == f` / `fp != f`.
// In C the name decays to a function pointer; C#'s `&f` is an untyped method-group
// address, so dotcc casts it to the other operand's fn-ptr type. Oracled.
#include <stdio.h>

typedef int (*Op)(int);
static int inc(int x) { return x + 1; }
static int dec(int x) { return x - 1; }

int main(void) {
    Op f = inc;
    printf("%d %d\n", f == inc, f == dec);   // 1 0
    f = dec;
    printf("%d %d\n", f != inc, f != dec);   // 1 0
    return 0;
}
