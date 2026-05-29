// C23 `_Float128` printed at full quad precision via printf %Lf/%Le/%Lg.
// dotcc formats Float128 with its own correctly-rounded BigInteger decimal
// conversion (no narrowing to double). On aarch64 `long double` IS binary128,
// so gcc prints the same digits (it warns that %Lf wants `long double`, but
// the representation is identical) — giving the oracle parity. MSVC has no
// `_Float128` at all, so this fixture opts out of the MSVC oracle.
//
// 1/3 to 40 places shows binary128's ~34 significant digits, far beyond
// double's 15-16 — proving the value really is computed in quad.
#include <stdio.h>

int main() {
    _Float128 third = (_Float128)1 / 3;
    printf("%.40Lf\n", third);

    _Float128 sevenths = (_Float128)2 / 7;
    printf("%.34Le\n", sevenths);

    _Float128 eighth = (_Float128)1 / 8;
    printf("%Lg\n", eighth);

    printf("%.2Lf\n", (_Float128)10 / 4);
    return 0;
}
