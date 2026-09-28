/* GH #245: declaration specifiers come in any order (C11 6.7p1). A storage
 * class that is not first is obsolescent (6.11.5) but valid, and CPython writes
 * `inline static` (Objects/typeobject.c, the HACL* krml headers). Every storage
 * class and function specifier here sits after a type, a qualifier or another
 * specifier. */
#include <stdio.h>

int static counter;                  /* static after the type */
const static int step = 2;           /* static after a qualifier */
int typedef word, *word_ptr;         /* typedef after the type, two declarators */
static int *static_ptr;              /* static, then a pointer declarator */
int extern later;                    /* extern after the type */

inline static int bump(void) { counter += step; return counter; }
inline static int *counter_ptr(void) { return &counter; }
static inline int twice(int v) { return 2 * v; }

int main(void) {
    int static calls = 0;            /* a static local, the specifier second */
    unsigned register total = 0;     /* register after the type */
    word w = 3;
    word_ptr p = &w;
    static_ptr = counter_ptr();
    for (int i = 0; i < 3; i++) { calls++; total += (unsigned)bump(); }
    {
        int extern later;            /* block-scope extern, the specifier second */
        printf("%d %d %d %d %u %d\n", counter, *static_ptr, calls, twice(*p), total, later);
    }
    return 0;
}

int later = 7;
