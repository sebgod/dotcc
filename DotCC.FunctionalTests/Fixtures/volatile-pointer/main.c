#include <stdio.h>

/* Phase V2: pointer-to-volatile. `volatile int *p` — the POINTEE is volatile, so
 * `*p` and `p[i]` fence (Volatile.Read / Volatile.Write); the pointer object itself
 * is plain. Covers a local volatile pointer, a `volatile T *` function parameter
 * (the MMIO idiom), and the subscript form. Single-threaded the values are
 * deterministic — the point is the emitted C# routes every pointee access through
 * the Volatile API. */

static void poke(volatile int *reg, int v) {
    *reg = v;            /* fence through a volatile-pointer parameter */
}

int main(void) {
    int mem[4] = {0, 0, 0, 0};
    volatile int *p = mem;
    *p = 10;             /* Volatile.Write(ref *p) */
    p[1] = 20;           /* Volatile.Write(ref p[1]) */
    poke(&mem[2], 30);
    p[3] = p[0] + p[1] + mem[2];   /* the pointee reads fence */
    printf("%d %d %d %d\n", p[0], p[1], p[2], p[3]);
    return 0;
}
