#include <stdio.h>

/* C lets you cast an out-of-range integer CONSTANT to a narrower / unsigned
   type — it truncates (mod 2^width). C# rejects such a constant cast (CS0221)
   unless wrapped in `unchecked(...)`. dotcc wraps an integer cast whose operand
   is a constant expression that isn't provably in range. This covers Lua's
   pervasive `cast_byte(~mask)` (a bit-clear), `(size_t)-1`, and
   `cast_int(MAX_SIZET / sizeof(t))` idioms — including the cases dotcc can't
   fold to a value (a uint-modular shift, a ulong-wide divide), where the
   ConstExpr flag (not the folded value) is what gates the wrapper. */
typedef unsigned char lu_byte;

int main(void) {
    lu_byte a = (lu_byte)(~(1 << 6));          /* ~64 = -65 (int), truncates to 191 */
    lu_byte b = (lu_byte)(-2);                 /* -2 truncates to 254 */
    lu_byte c = (lu_byte)(~((~0u) << 3));      /* uint-modular: (~0u)<<3 = 0xFFFFFFF8, ~ = 7 */
    int     d = (int)((~0UL) / sizeof(int));   /* ulong-max / 4 = 0x3FFF…, (int) low word = -1 */
    printf("%d %d %d %d\n", (int)a, (int)b, (int)c, d);
    return 0;
}
