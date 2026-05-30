/* The C comma operator: evaluate each operand left-to-right (a sequence point
 * at every comma), value is the last. C# has no comma operator, so dotcc lowers
 * the value form `(a, b, c)` to a tuple `(a, b, c).Item3` (C# evaluates tuple
 * elements left-to-right), and the statement form `a, b;` to sequential
 * statements. Call-argument commas (e.g. printf's) stay separators — only the
 * full-expression positions `( … )` and `… ;` use the operator. gcc is oracle. */
#include <stdio.h>

int main(void) {
    int a = 0, b = 0, c = 0;

    int x = (a = 1, b = 2, a + b);    /* value form: x = 3, with a=1, b=2 */
    printf("x=%d a=%d b=%d\n", x, a, b);

    a = 7, b = 8;                      /* statement form: a=7; b=8; */
    printf("a=%d b=%d\n", a, b);

    int i = 5, j = 10;
    c = (i++, j++, i + j);             /* c = 17, then i=6, j=11 */
    printf("c=%d i=%d j=%d\n", c, i, j);

    for (i = 0, j = 9; i < j; i++, j--) { }   /* for-clause commas (separate) */
    printf("i=%d j=%d\n", i, j);

    return 0;
}
