/* A conditional operator whose two arms render to different C# types: one arm
 * is a comparison (`x == 1` — C type int, but dotcc emits it as the CBool value
 * type) and the other a plain int. In a boolean position the arms must agree on
 * a single C# type, else a target-typed `Cond.B(cond ? a : b)` is ambiguous.
 * (chibi srfi/69 hash.c: `sexp_pointerp(o) ? (tag==SYMBOL) : !sexp_fixnump(o)`.) */
#include <stdio.h>

int classify(int x) {
    /* inner ternary sits in the OUTER ternary's condition (a Cond.B position);
     * then-arm is a comparison, else-arm a plain int. */
    return (x > 0 ? (x == 1) : 0) ? 100 : 200;
}

int main(void) {
    printf("%d %d %d\n", classify(1), classify(5), classify(-1));  /* 100 200 200 */
    return 0;
}
