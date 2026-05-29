// C23 `_Float128` (IEEE-754 binary128) — declaration, arithmetic via
// operators, integer literal widening, and narrowing casts. dotcc lowers
// `_Float128` to its software DotCC.Libc.Float128; gcc (aarch64) maps it to
// binary128 `long double`-equivalent, so the observable result matches.
//
// Uses only `_Float128` (not the GNU `__float128` spelling, which gcc only
// defines on x86) so the gcc oracle can compile this verbatim.
#include <stdio.h>

int main() {
    _Float128 a = 3;
    _Float128 b = 4;
    _Float128 c = a * a + b * b;   // 9 + 16 = 25
    _Float128 d = c - 5;           // 20
    _Float128 e = (a + b) * (a + b); // 49

    printf("%d %d %d\n", (int)c, (int)d, (int)e);
    return 0;
}
