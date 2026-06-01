/* A function-specifier (`inline` / `_Noreturn`) run preceding a TYPE_NAME return
 * type — Lua's `l_sinline Table *gettable(...)` where `l_sinline` expands to
 * `static inline`. The declaration-specifier sequence `[static] inline <typedef>`
 * couldn't form a Type before: a typedef-name is a separate Type production, not a
 * TypeSpec, so the spec-list couldn't absorb it. dotcc now composes them, keeping
 * the inline flag ([MethodImpl(AggressiveInlining)]) — even through a pointer
 * return (`inline Cell *`). gcc/MSVC agree on the observable behaviour. */
#include <stdio.h>

typedef struct { int v; } Cell;
typedef int Count;

/* static inline + pointer-returning typedef base (the l_sinline shape) */
static inline Cell *bump(Cell *c) { c->v += 1; return c; }

/* static inline + non-pointer typedef base. (A plain non-static `inline`
 * definition is only an inline *candidate* in C99 — with no external definition
 * gcc -O0 leaves an undefined reference at link, so the portable single-TU form
 * is `static inline`, which is also Lua's `l_sinline`. The plain-`inline`-before-
 * typedef path is covered in-process by the unit test, where Roslyn links it.) */
static inline Count twice(Count n) { return n * 2; }

int main(void) {
    Cell c;
    c.v = 10;
    bump(&c);
    bump(&c);
    printf("%d\n", c.v);          /* 12 */
    printf("%d\n", twice(21));    /* 42 */
    return 0;
}
