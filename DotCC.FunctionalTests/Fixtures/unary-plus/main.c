/* Unary plus (C89) — a value-preserving no-op beyond the integer promotions.
 * Patterns from chibi-scheme: `+1` as a call argument (vm.c's
 * sexp_inc_context_depth(ctx, +1)) and the branch-free sign trick
 * `(+1 | (x >> 63))` (sexp_fx_sign). Also `sizeof(S) + n`, which needs the
 * SizeofFolder to treat `+` as a possible-unary follow token (else the parser
 * reads a cast `sizeof((S) + n)`). */
#include <stdio.h>

struct pair { long a, b; };

static long sign(long x) {
    return (+1 | (x >> (sizeof(long) * 8 - 1)));
}

static long add(long base, long delta) { return base + delta; }

int main(void) {
    printf("%ld\n", +5L);
    printf("%ld\n", add(10, +1));
    printf("%ld\n", sign(-42));
    printf("%ld\n", sign(42));
    printf("%lu\n", (unsigned long)(sizeof(struct pair) + +4));
    double d = +1.5;
    printf("%g\n", +d);
    return 0;
}
