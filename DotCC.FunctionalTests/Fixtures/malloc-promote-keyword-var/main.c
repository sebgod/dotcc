/* The malloc -> stack-value peephole must interoperate with C#-keyword
 * @-escaping. Here the malloc'd pointer is named `new` (a C# keyword, valid C).
 * It's used only via `->` and freed, so it promotes to a stack value — and the
 * promoted declaration, the `.` member accesses, and the dropped free must all
 * agree on the @-escaped name `@new`. (The maps that drive the peephole are
 * keyed by the raw C name `new`, while the emitted text is `@new`; the lookups
 * unescape before matching.) gcc accepts `new` verbatim; result is 7. */
#include <stdio.h>
#include <stdlib.h>

struct S { int x; };

int main(void) {
    struct S* new = (struct S*)malloc(sizeof(struct S));
    new->x = 7;
    printf("%d\n", new->x);
    free(new);
    return 0;
}
