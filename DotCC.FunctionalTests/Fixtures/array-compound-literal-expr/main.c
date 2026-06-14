#include <stdio.h>

/* C99 array compound literals in NON-initializer positions — a call argument, a
   direct subscript, an arithmetic operand, and a return value. dotcc hoists each
   to a block-local pointer temp (C's block-scoped automatic storage), so the
   array outlives the use. (MSVC's C frontend has spotty C99 compound-literal
   support — gcc is the oracle.) */

static int sum(const int *a, int n) {
    int s = 0;
    for (int i = 0; i < n; i++) { s += a[i]; }
    return s;
}

static int third(void) {
    return ((int[]){100, 200, 300})[2];   /* read 300 back out of the literal */
}

int main(void) {
    printf("sum=%d\n", sum((int[]){1, 2, 3, 4}, 4));   /* call argument */
    printf("mid=%d\n", ((int[]){5, 6, 7})[1]);         /* subscripted directly */
    int t = sum((int[]){10, 20}, 2) * 2;               /* nested in arithmetic */
    printf("t=%d\n", t);
    printf("third=%d\n", third());                     /* returned via a literal */
    return 0;
}
