/* C `_Bool` initialised/assigned from a POINTER: the store coerces the pointer
 * to its truthiness — `_Bool b = p;` means `p != NULL`. This is the same scalar
 * normalization C applies for ints, but pointers have no implicit C# path to
 * CBool, so dotcc routes every `_Bool` store (decl init AND return in a
 * bool-returning function) through `Cond.B(...)`: B(void*) -> `!= null`,
 * B(int) -> `!= 0`, B(bool) -> identity. gcc confirms the values. */
#include <stdbool.h>
#include <stdio.h>
#include <stddef.h>

/* bool-returning function returning a raw pointer: result is pointer != NULL */
bool nonnull(int *p) {
    return p;
}

int main(void) {
    int n = 7;
    int *live = &n;
    int *dead = NULL;

    bool a = live;          /* &n != NULL -> 1 */
    bool b = dead;          /* NULL       -> 0 */
    bool c = (int *)0;      /* explicit null pointer -> 0 */
    bool d = "literal";     /* string literal address is non-NULL -> 1 */

    printf("%d %d %d %d\n", a, b, c, d);                /* 1 0 0 1 */
    printf("%d %d\n", nonnull(live), nonnull(dead));    /* 1 0 */
    return 0;
}
