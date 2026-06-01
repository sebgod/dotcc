/* Argument prescan (C11 §6.10.3.1): a macro-call argument is FULLY expanded in
 * the call-site context before it's substituted into the body. The trap —
 * reduced from Lua's `getstr(tsvalue(o))` string accessor — is a macro whose
 * body re-uses the SAME macro (`cast`) that the argument also expands to.
 * Without prescan, `cast` lands on the hide set while the body's own `cast(...)`
 * is rescanned, so the argument's inner `cast` is left literal and parses as a
 * type name in expression position (a hard parse error). gcc/MSVC agree on 24. */
#include <stdio.h>

#define cast(t, e)  ((t)(e))
#define to_long(x)  cast(long, (x))
/* getval re-uses `cast` in its own body while its argument also expands to a
 * cast(...): the exact hide-set collision the prescan resolves. */
#define getval(p)   (cast(int, p) + 1)
#define wrap(v)     getval(to_long(v))

int main(void) {
    long base = 20;
    /* to_long(23) -> (long)((23)); getval(23) -> (int)(...) + 1 = 24 */
    int r = wrap(base + 3);
    printf("%d\n", r);
    return 0;
}
