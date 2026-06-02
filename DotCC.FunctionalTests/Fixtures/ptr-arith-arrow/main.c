/* Postfix `.`/`->` on a COMPOUND base must keep its parens so the member
 * operator binds to the whole base, not its trailing operand. `(p - 1)->m`
 * must not become `p - 1->m` (which parses as `p - (1->m)`). Covers the shapes
 * Lua's `getlastfree(t) = ((cast(Limbox*,(t)->node) - 1)->lastfree)` relies on:
 * pointer arithmetic, a deref, and a cast as the member base. gcc is the oracle. */
#include <stdio.h>

typedef struct { int v; } S;

int main(void) {
  S arr[3];
  S *p = &arr[2];
  S **pp = &p;
  void *raw;

  arr[0].v = 10;
  arr[1].v = 20;
  arr[2].v = 30;

  /* (p - 1)->v : pointer arithmetic as the arrow base */
  printf("%d\n", (p - 1)->v);            /* arr[1].v = 20 */

  /* (*pp)->v : deref as the arrow base */
  printf("%d\n", (*pp)->v);              /* p->v = 30 */

  /* ((S*)raw)->v : cast as the arrow base */
  raw = &arr[0];
  printf("%d\n", ((S*)raw)->v);          /* arr[0].v = 10 */

  /* (arr + 1)->v : array+int as the arrow base */
  printf("%d\n", (arr + 1)->v);          /* arr[1].v = 20 */

  /* chained: ((p - 1) - 1)->v */
  printf("%d\n", ((p - 1) - 1)->v);      /* arr[0].v = 10 */

  return 0;
}
