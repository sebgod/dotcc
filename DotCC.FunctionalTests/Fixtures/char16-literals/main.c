/* C11 char16_t (<uchar.h>) end-to-end. dotcc lowers char16_t to C# `char` (a
 * 16-bit UTF-16 code unit), so this exercises every lowering path at runtime:
 *
 *   - u"…" expression literal  -> a pooled, pinned Libc.L16 char*  (p)
 *   - char16_t buf[N] = u"…"   -> a mutable char stackalloc copy   (buf)
 *   - file-scope char16_t[]    -> pinned Libc.GlobalArrayFrom<char> (mg)
 *   - const file-scope char16_t[] -> zero-copy RVA Libc.L<char>     (cg)
 *   - u'x' char constant       -> a char16_t-typed value           (c)
 *   - char16_t arithmetic      -> promotes to int, narrows on store (c + 1)
 *
 * Wide stdout I/O is out of scope, so every code unit is printed as an int —
 * the value is endianness-independent, so a real compiler (which keeps the
 * heap allocation and the native char16_t type) produces the identical output.
 */

#include <uchar.h>
#include <stdio.h>

const char16_t cg[] = u"AB";   /* const global -> RVA Libc.L<char>            */
char16_t mg[] = u"cd";         /* mutable global -> Libc.GlobalArrayFrom<char> */

int main(void) {
    const char16_t *p = u"hi\x1234";   /* expression literal -> Libc.L16       */
    char16_t buf[8] = u"xy";           /* local stackalloc, NUL + zero-padded  */
    char16_t c = u'Z';                 /* char16_t constant (0x5A == 90)       */

    buf[2] = u'!';                     /* mutate the writable copy (33)        */
    c = c + 1;                         /* int arithmetic, narrows back to char */

    printf("p: %d %d %d\n", (int)p[0], (int)p[1], (int)p[2]);
    printf("buf: %d %d %d %d\n", (int)buf[0], (int)buf[1], (int)buf[2], (int)buf[3]);
    printf("cg: %d %d\n", (int)cg[0], (int)cg[1]);
    printf("mg: %d %d\n", (int)mg[0], (int)mg[1]);
    printf("c: %d\n", (int)c);
    return 0;
}
