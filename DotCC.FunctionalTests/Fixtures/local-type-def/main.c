/* Block-scope (local) AGGREGATE TYPE DEFINITIONS — a struct/union/enum defined
 * inside a function body, used as a statement. C allows this (a type has no
 * storage); dotcc hoists the definition into the top-level type section, deduped
 * by tag, and the statement emits nothing. Motivated by Lua lstrlib's local
 * alignment probe `struct cD { char c; union { LUAI_MAXALIGN; } u; };`. */
#include <stdio.h>

int main(void) {
    /* block-scope struct definition (with a nested anonymous-union member, like
     * Lua's alignment probe) */
    struct cD { char c; union { double d; long l; void *p; } u; };
    /* block-scope enum definition */
    enum local_e { LA, LB = 5, LC };
    /* block-scope union definition */
    union local_u { int i; char c[4]; };

    struct cD x;
    x.c = 'z';
    x.u.l = 42;
    printf("enum: %d %d %d\n", LA, LB, LC);                  /* 0 5 6 */
    printf("union size: %d\n", (int)sizeof(union local_u));  /* 4 */
    printf("x: %c %ld\n", x.c, x.u.l);                       /* z 42 */
    return 0;
}
