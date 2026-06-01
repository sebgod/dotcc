#include <stdio.h>
#include <signal.h>

/* dotcc's header-only <signal.h>: sig_atomic_t (→ int) + the SIG* macros. Combined
 * with the volatile flag idiom (block-scope, so it's fenced) — the shape of Lua's
 * `volatile sig_atomic_t trap;`. signal() itself is deferred to the REPL phase. */

int main(void) {
    volatile sig_atomic_t flag = 0;
    flag = SIGINT;                       /* a handler would set this */
    if (flag == SIGINT) { printf("caught SIGINT (%d)\n", (int)flag); }
    flag = 0;
    printf("flag=%d sigterm=%d sigsegv=%d\n", (int)flag, SIGTERM, SIGSEGV);
    return 0;
}
