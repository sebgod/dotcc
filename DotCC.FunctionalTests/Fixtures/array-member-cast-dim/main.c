/* A struct array-member whose dimension is a CAST of a constant expression —
 * Lua's `char space[BUFVFS]` where `BUFVFS = cast_uint(LUA_IDSIZE + … )`, i.e.
 * `((unsigned int)(219))`. dotcc folds the cast-of-constant to the literal a C#
 * `fixed`/`[InlineArray]` buffer needs. And `sizeof(s.buf)` of a 1-D array member
 * yields count*sizeof(element) (the member carries its Arr CType), not the decayed
 * pointer size — Lua's `buff->buffsize = sizeof(buff->space)`. gcc/MSVC agree. */
#include <stdio.h>

#define WIDEN(x)  ((unsigned int)(x))
#define BUFSZ     WIDEN(10 + 6)        /* cast of a constant arithmetic expr -> 16 */

struct Buf {
    char *b;
    char space[BUFSZ];
};

int main(void) {
    struct Buf buf;
    buf.b = buf.space;                          /* fixed buffer decays to a pointer */
    printf("%d\n", (int)sizeof(buf.space));     /* 16 (count * 1) */
    printf("%d\n", (int)BUFSZ);                 /* 16 */
    buf.space[0] = 'A';
    buf.space[15] = 'Z';
    printf("%c%c\n", buf.space[0], buf.space[15]);   /* AZ */
    return 0;
}
