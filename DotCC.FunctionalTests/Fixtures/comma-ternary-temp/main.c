/* A comma whose leading operand is a void call, inside a ternary arm (Goose's
   bounds check): at a statement that can take temps it lowers to an if/else
   over one, the call conditional as C has it, with no closure (which could not
   capture `local`, whose address is taken). Nested ternaries too. */
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
static int fails;
static void fail(int64_t i, int64_t n) { printf("fail %lld %lld\n", (long long)i, (long long)n); fails++; }
#define IDX(i, n) ((uint64_t)(i) < (uint64_t)(n) ? (i) : (fail((int64_t)(i), (n)), (int64_t)0))
static int64_t get(int64_t *p, int64_t i, int64_t n) {
    int64_t local = 7;
    int64_t *q = &local;          /* address taken: a closure could not capture it */
    int64_t v = p[IDX(i, n)] + *q;
    if (p[IDX(i + 1, n)] > 0) v += 100;
    v += (i > 1 ? (fail(i, -1), local) : (i < 0 ? (fail(-1, i), 0) : 1));
    return v;
}
int main(void) {
    int64_t a[3] = {1, 2, 3};
    printf("%lld\n", (long long)get(a, 0, 3));
    printf("%lld\n", (long long)get(a, 2, 3));
    printf("%lld\n", (long long)get(a, 5, 3));
    printf("fails %d\n", fails);
    return 0;
}
