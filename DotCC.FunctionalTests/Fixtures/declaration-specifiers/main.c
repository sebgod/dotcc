/* Function specifiers and storage classes compose with any type (GH #223, C11
   6.7.1, 6.7.4): `inline const T f(…)` and `inline struct T f(…)` (CPython's
   pycore_pystate.h), `_Thread_local const` (import.c), `register` in a parameter
   (odictobject.c) and on locals. An empty declaration at file scope is ignored
   (`_Py_DECLARE_STR(...);` expanding to nothing, `};` after a function), and
   `return` and a `for` condition take a comma expression (symtable.c). */
#include <stdio.h>

typedef struct Pair { int a, b; } Pair;
typedef unsigned long size_type;
enum Mode { SLOW, FAST };

#define DECLARE_NOTHING(name)
DECLARE_NOTHING(first);

static Pair pairs[2] = { { 1, 2 }, { 3, 4 } };
static _Thread_local const Pair *current;

static inline const Pair *pick(int i) { return &pairs[i]; }
static inline struct Pair *second(void) { return &pairs[1]; }
static inline enum Mode mode_of(int fast) { return fast ? FAST : SLOW; }

static size_type sum(register const Pair *p, register size_type n)
{
    register size_type total = 0;
    for (register size_type i = 0; i < n; i++) { total += (size_type)(p[i].a + p[i].b); }
    return total;
};

static int calls = 0;
static int count(int v) { calls++; return v; }
static int last(int a, int b) { return count(a), count(b); }

int main(void)
{
    int i = 0;
    int l;
    current = &pairs[1];
    printf("%d %d %d\n", pick(0)->b, second()->a, (int)mode_of(1));
    printf("%lu %d\n", sum(pairs, 2), current->a);
    l = last(5, 7);
    printf("%d %d\n", l, calls);
    for (i = 0; i++, i < 3;) { }
    printf("%d\n", i);
    return 0;
}
