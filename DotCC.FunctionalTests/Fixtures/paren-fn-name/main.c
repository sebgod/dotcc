#include <stdio.h>

/* Parenthesized function declarator: `T (name)(args)` is identical to
 * `T name(args)` — the parens are pure grouping around the declarator name.
 * Lua's public headers wrap every API name this way to stop a same-named
 * function-like macro from being expanded at the declaration. */

int (add)(int a, int b);                       /* parenthesized prototype */
int (add)(int a, int b) { return a + b; }      /* parenthesized definition */
double (square)(double x) { return x * x; }    /* different return type */
int (answer)(void) { return 42; }              /* void params */

int main(void) {
    printf("add=%d square=%g answer=%d\n", add(2, 3), square(4.0), answer());
    return 0;
}
