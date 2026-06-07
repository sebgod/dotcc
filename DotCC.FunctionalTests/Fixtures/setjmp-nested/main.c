/* Nested setjmp/longjmp: a longjmp aimed at the OUTER env must skip the
   INNER setjmp's handler and resume at the outer one. This exercises the
   per-site token identity — with a shared/empty token, the inner handler
   would wrongly catch the outer-targeted jump. */
#include <setjmp.h>
#include <stdio.h>

static jmp_buf outer;
static jmp_buf inner;

int main(void) {
    if (setjmp(outer) == 0) {
        if (setjmp(inner) == 0) {
            printf("armed\n");
            longjmp(outer, 7);          /* jump PAST inner, straight to outer */
            printf("after longjmp (unreachable)\n");
        } else {
            printf("inner handler (should NOT run)\n");
        }
    } else {
        printf("outer handler\n");      /* expected landing spot */
    }
    printf("done\n");
    return 0;
}
