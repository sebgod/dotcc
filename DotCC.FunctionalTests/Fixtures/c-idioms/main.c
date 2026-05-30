/* Three everyday C idioms dotcc gained by driving a real program through it:
 *   - const qualifiers (`const int`, `const char *`)
 *   - the `for (;;)` infinite-loop-with-break form (empty for-clauses)
 *   - a trailing comma in an initializer list (`{ …, }`)
 * All compile and run identically to C; const is dropped, the empty condition
 * becomes `true`, and the trailing comma is ignored. */
#include <stdio.h>

const int LIMIT = 5;

int main(void) {
    const char *label = "sum";
    int squares[] = { 1, 4, 9, 16, 25, };   /* trailing comma */
    int i = 0;
    int total = 0;
    for (;;) {                               /* empty for-clauses + break */
        if (i >= LIMIT) break;
        total += squares[i];
        i++;
    }
    printf("%s = %d\n", label, total);       /* sum = 55 */
    return 0;
}
