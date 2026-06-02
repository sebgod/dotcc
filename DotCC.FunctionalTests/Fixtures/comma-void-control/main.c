/* The comma operator in a CONTROLLING expression, where a non-last operand is a
 * VOID side-effect — Lua's llex.c idiom:
 *     while (cast_void(save_and_next(ls)), lisxdigit(ls->current)) { … }
 * where save_and_next = (save(ls,…) /void/, next(ls) /assignment/) and cast_void
 * is (void)(…). A void operand can't be a C# tuple element, so dotcc lifts the
 * non-last operands into statements and tests the last with Cond.B. Exercised in
 * while / if / switch positions plus a standalone (void) discard. gcc/MSVC agree. */
#include <stdio.h>

#define cast_void(x)  ((void)(x))

static int log_[16];
static int li = 0;
static void record(int v) { log_[li++] = v; }   /* void side effect */

static int i = 0;
#define step()              (i = i + 1)               /* assignment (statement-valid) */
#define record_and_step()   (record(i), step())       /* comma: void record, then assign */

int main(void) {
    /* while: each iteration records i then steps it; test i < 5. */
    while (cast_void(record_and_step()), i < 5) { }
    printf("%d %d\n", i, li);                 /* 5 5 */
    for (int k = 0; k < li; k++) printf("%d", log_[k]);
    printf("\n");                             /* 01234 */

    /* if with a comma controlling expr: side effect then test. */
    int x = 0;
    if (x = 7, x > 3) printf("big %d\n", x);  /* big 7 */
    else printf("small\n");

    /* switch with a comma controlling expr. */
    int y = 0;
    switch (y = 2, y) {
        case 2: printf("two\n"); break;       /* two */
        default: printf("other\n"); break;
    }

    /* standalone (void) discard of a comma — pure side effects. */
    cast_void((record(99), step()));
    printf("%d %d\n", i, log_[li - 1]);       /* 6 99 */
    return 0;
}
