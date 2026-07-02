/* _Alignof (C11) + the <stdalign.h> lowercase macros, folded at compile time
 * by dotcc's layout model; _Alignas accepted (constraint-checked) and ignored
 * on the managed target. The printed values are pinned against gcc on LP64
 * SysV targets (x86-64 / aarch64 Linux); MSVC opts out — LLP64 `long` aligns
 * to 4 there. Default -std=c17, so the lowercase spellings exercise the
 * <stdalign.h> macro path (not the C23 keyword promotion). */
#include <stdio.h>
#include <stdalign.h>

struct P { char c; int i; };
struct Q { char c; double d; };

/* the fold is an integer constant expression — compose with _Static_assert */
_Static_assert(alignof(int) == 4, "int alignment");
_Static_assert(alignof(struct Q) == 8, "struct = max field alignment");

int main(void) {
    _Alignas(16) int over = 42;      /* stricter than natural: accepted, ignored */
    alignas(double) int asdbl = 7;   /* align-as-type form, macro spelling */

    printf("%d %d %d %d %d %d %d %d\n",
        (int)alignof(char), (int)alignof(short), (int)alignof(int),
        (int)alignof(long), (int)alignof(double), (int)alignof(void*),
        (int)alignof(struct P), (int)alignof(struct Q));
    printf("%d %d\n", over, asdbl);
    return 0;
}
