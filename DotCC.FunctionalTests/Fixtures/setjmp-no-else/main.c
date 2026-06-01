/* setjmp in an `if` WITHOUT an `else` — Lua's `LUAI_TRY` shape:
     #define LUAI_TRY(L,c,f,ud)  if (setjmp((c)->b) == 0) ((f)(L, ud))
   The guard runs the body; a longjmp out of it unwinds back and is simply
   swallowed (no recovery branch), then execution continues after the `if`.
   dotcc lowers `if (setjmp(env) == 0) STMT;` (no else) to
   `try { STMT } catch (matching) { }`. Both the success path (no longjmp) and
   the unwind path are exercised here; gcc/MSVC agree on the observable order. */

#include <stdio.h>
#include <setjmp.h>

jmp_buf env;

static void worker(int x) {
    printf("worker: enter %d\n", x);
    if (x < 0) {
        printf("worker: bailing\n");
        longjmp(env, 1);            /* unwind past the guard */
    }
    printf("worker: done %d\n", x);
}

/* The guard has no else clause — the unwind path just falls through. */
static void guarded(int x) {
    if (setjmp(env) == 0) {
        worker(x);
        printf("guard: completed\n");
    }
    printf("guard: after\n");        /* always reached */
}

int main(void) {
    guarded(5);                      /* success path */
    printf("===\n");
    guarded(-1);                     /* longjmp unwinds, swallowed */
    return 0;
}
