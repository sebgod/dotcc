#include <stdio.h>

/* C23 typeof — yields the type of an expression or a type. dotcc reads the
   operand's synthesized CType (the same layer sizeof(expr) uses) for the expr
   form, and unwraps the type form. typeof_unqual behaves identically (dotcc
   drops qualifiers). */

int main(void) {
    int x = 5;
    typeof(x) y = 10;          /* y : int */
    typeof(int) z = 3;         /* z : int */
    double d = 2.5;
    typeof(d) e = d + 1.0;     /* e : double */
    int *p = &x;
    typeof(p) q = p;           /* q : int* */
    typeof_unqual(x) u = 7;    /* same as typeof(x) here */

    printf("%d %d %.1f %d %d\n", y, z, e, *q, u);
    return 0;
}
