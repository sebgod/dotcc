/* sizeof of an EXPRESSION through member access, pointer arithmetic, and comma —
 * the cluster Lua's serialization (ldump/lundump) and state sizing (lstate) need.
 * dotcc synthesizes a partial CType up each expression; member access now carries
 * its field's type (so `sizeof(p->f)`, nested chains `p->in.g`, and `sizeof(*p->f)`
 * / `sizeof(p->f[i])` resolve), additive pointer arithmetic carries the decayed
 * pointer (so `sizeof((buf+n)[0])`), and a value-context comma carries its last
 * operand's type. Sizes here are ABI-stable (char=1, int=4, double=8) so gcc,
 * MSVC, and dotcc all agree. */
#include <stdio.h>

struct Inner { int g; double d; };
struct Node { int *code; struct Inner in; char tag; };

int main(void) {
    struct Node nd;
    struct Node *p = &nd;
    int arr[4];
    char buf[16];
    int n = 2;
    printf("%d\n", (int)sizeof(p->tag));        /* char   -> 1 */
    printf("%d\n", (int)sizeof(p->code[0]));    /* int    -> 4 */
    printf("%d\n", (int)sizeof(*p->code));      /* int    -> 4 */
    printf("%d\n", (int)sizeof(p->in.g));       /* int    -> 4 */
    printf("%d\n", (int)sizeof(p->in.d));       /* double -> 8 */
    printf("%d\n", (int)sizeof((buf + n)[0]));  /* char   -> 1 */
    printf("%d\n", (int)sizeof((arr + 1)[0]));  /* int    -> 4 */
    printf("%d\n", (int)sizeof((&n)[0]));       /* int    -> 4 */
    return 0;
}
