/* A `setjmp` guard whose protected body is a SINGLE (braceless) statement —
 * Lua's `LUAI_TRY(L,c,a) = if (setjmp((c)->b) == 0) { a }` reduces, after macro
 * expansion, to `if (setjmp(env) == 0) (f)(L, ud);`. dotcc's setjmp->try/catch
 * rewrite must brace the try BODY (`try stmt;` is invalid C# — it needs
 * `try { stmt; }`). Both the with-else and no-else forms are exercised. */
#include <setjmp.h>
#include <stdio.h>

static jmp_buf env;
static void may_throw(int x) { if (x) longjmp(env, 7); }

int main(void) {
    int caught = 0, ran = 0;

    /* with-else, braceless single-statement try body */
    if (setjmp(env) == 0)
        may_throw(1);          /* longjmp -> setjmp returns 7 -> else */
    else
        caught = 1;
    printf("caught=%d\n", caught);   /* 1 */

    /* no-else, braceless single-statement try body */
    if (setjmp(env) == 0)
        ran = 1;               /* no longjmp -> runs normally */
    printf("ran=%d\n", ran);         /* 1 */
    return 0;
}
