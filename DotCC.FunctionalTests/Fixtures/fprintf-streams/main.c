// fprintf to the standard streams. In --target=wat there is no libc FILE*
// runtime, so the backend maps stdout/stderr onto WASI fds (1/2) and reuses the
// printf-family inline expansion. This fixture keeps all observable output on
// stdout (printf + fprintf(stdout)); the stderr routing is unit-pinned. Also
// valid C99 — clang/gcc produce identical stdout.
#include <stdio.h>

int main(void) {
    printf("printf %d %s\n", 1, "one");
    fprintf(stdout, "fprintf %d %s\n", 2, "two");
    fprintf(stdout, "widths %c|%05d\n", 'A', 42);
    return 0;
}
