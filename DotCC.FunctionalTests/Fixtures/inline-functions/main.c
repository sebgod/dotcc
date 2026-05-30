#include <stdio.h>

/* C99 `inline` function specifier. dotcc maps it to
   [MethodImpl(MethodImplOptions.AggressiveInlining)] — a real JIT hint, the
   faithful lowering of C's "please inline this".

   This fixture uses the two single-TU-linkable forms (a plain `inline`
   definition with no external definition is a link error under gcc -O0, since
   it provides only an inline definition):
     - `static inline`  — internal linkage, always emits a callable copy
     - `extern inline`   — provides the external definition (C99 §6.7.4)
   Plain `inline` (accepted + lowered identically by dotcc) is covered in the
   unit tests, which check emit shape rather than cross-compiler linkage. */

static inline int square(int x) { return x * x; }

static inline int cube(int x) { return x * x * x; }

extern inline long add3(long a, long b, long c) { return a + b + c; }

int main(void) {
    printf("%d\n", square(5));
    printf("%d\n", cube(3));
    printf("%ld\n", add3(10, 20, 30));
    return 0;
}
