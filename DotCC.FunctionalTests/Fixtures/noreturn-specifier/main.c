#include <stdio.h>

/* C11 `_Noreturn` function specifier → C# [DoesNotReturn] (a real flow-analysis
   hint, the faithful lowering of "control never comes back"). The body is an
   infinite loop so it genuinely never returns; it's called only on a
   never-taken path so the program still terminates normally. */

_Noreturn void die(const char *msg) {
    printf("fatal: %s\n", msg);
    for (;;) { }   /* never returns */
}

int main(void) {
    int x = 5;
    if (x < 0) die("x went negative");   /* never taken */
    printf("%d\n", x);
    return 0;
}
