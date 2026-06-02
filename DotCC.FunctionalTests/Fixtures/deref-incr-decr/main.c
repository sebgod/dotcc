/* Postfix `++`/`--` on a parenthesised compound lvalue — `(*p)++` / `(*p)--`.
 * These must keep their parens: `(*p)++` is "increment the pointee", but if the
 * parens are dropped to `*p++` it becomes `*(p++)` — increment the POINTER, wrong
 * value AND not a valid C# statement-expression. dotcc routes the postfix base
 * through PostfixBase (the same fix as the `.`/`->` compound base, Phase 4y).
 * `*p++` (genuine pointer post-increment, the common C idiom) is unaffected. */
#include <stdio.h>

int main(void) {
    int n = 5;
    int *p = &n;
    (*p)++;                 /* pointee: 5 -> 6 */
    (*p)--;                 /* pointee: 6 -> 5 */
    (*p)++;                 /* 5 -> 6 */
    printf("n=%d\n", n);    /* 6 */

    /* used in an expression too (not just a statement) */
    int arr[3] = {10, 20, 30};
    int *q = arr;
    int first = (*q)++;     /* first=10, arr[0]=11 */
    printf("first=%d arr0=%d\n", first, arr[0]);   /* first=10 arr0=11 */

    /* genuine pointer post-increment still walks the array */
    int sum = 0;
    int *r = arr;
    sum += *r++;            /* read arr[0]=11, r->arr[1] */
    sum += *r++;            /* read arr[1]=20, r->arr[2] */
    printf("sum=%d\n", sum);   /* 31 */
    return 0;
}
