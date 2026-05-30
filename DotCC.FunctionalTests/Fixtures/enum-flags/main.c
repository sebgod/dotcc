/* Bit-flag enums: the common C idiom where enumerators are powers of two and
 * combined / tested with bitwise operators. dotcc lowers `enum Mode` to a real
 * C# enum, so each enum operand of a bitwise op decays to (int) (C#'s `enum &
 * int` is an error, unlike C), the int result is cast back to the enum at an
 * enum-typed store, and `|=` expands to `m = (Mode)((int)m | …)`. gcc confirms. */
#include <stdio.h>

enum Mode {
    READ  = 1,
    WRITE = 2,
    EXEC  = 4
};

int main(void) {
    enum Mode m = READ | WRITE;          /* 1 | 2 = 3 */
    m |= EXEC;                           /* 3 | 4 = 7 */
    int has_write = (m & WRITE) != 0;    /* 7 & 2 = 2 -> 1 */
    enum Mode only = m & ~EXEC;          /* 7 & ~4 = 3 */
    printf("%d %d %d\n", m, has_write, only);   /* 7 1 3 */
    return 0;
}
