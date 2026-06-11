/* A goto-target label that sits BETWEEN two cases and FALLS THROUGH into the
 * next one — the shape chibi-scheme's VM dispatch loop relies on:
 *
 *     case OP_NOOP: break;
 *   call_error_handler:        // reached only via `goto`, from other cases
 *     <set up the exception>   // no break/return -> FALLS THROUGH ...
 *     case OP_RAISE:           // ... into this case's body (the raise logic)
 *       <propagate> break;
 *
 * C# has no implicit case fall-through and can't fall from an out-of-switch
 * hoisted handler back INTO a case, so dotcc relocates the successor case
 * wholesale right after the handler and redirects its `case` to a goto. If that
 * fall-through is dropped (the handler jumps PAST the switch instead), the next
 * case's body is skipped — in chibi that silently swallowed every raised
 * exception, leaving the VM stack corrupt (AccessViolation in sexp_apply on the
 * next op). This fixture pins the fall-through. */
#include <stdio.h>

static int run(int op) {
    int r = 0;
    switch (op) {
        case 0:
            r = 100;
            break;
        handler:                 /* shared label, jumped to from case 2 */
            r += 1;              /* "prologue" — falls through into case 1 */
        case 1:
            r += 10;             /* the body both entry paths converge on */
            break;
        case 2:
            r += 1000;
            goto handler;        /* run the handler, then fall into case 1 */
        default:
            r = -1;
    }
    return r;
}

int main(void) {
    /* run(0)=100; run(1)=10; run(2)=1000+1+10=1011; run(9)=-1 */
    printf("%d %d %d %d\n", run(0), run(1), run(2), run(9));
    return 0;
}
