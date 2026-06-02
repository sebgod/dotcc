/* A value-context comma whose LEADING operand is a void guard ternary — Lua's
 * `luaM_newvectorchecked(L,n,t)` = `(luaM_checksize(L,n,sizeof(t)),
 * luaM_newvector(L,n,t))`, where `luaM_checksize` = `(test ? luaM_toobig(L) :
 * cast_void(0))` is void. A void value can't be a C# tuple element, so dotcc
 * HOISTS the leading operand(s) as statements and keeps the last as the value
 * (`EmitContent.SeqExpr`) at the statement-level sink — an assignment or a
 * `return`. */
#include <stdio.h>
#include <stdlib.h>

static int checks = 0;
static void toobig(void) { printf("TOO BIG\n"); exit(1); }   /* the guard's throw path */
#define cast_void(x)   ((void)(x))
#define checksize(n)   ((n) > 1000000 ? toobig() : cast_void(++checks))
#define newvec(n)      ((int*)malloc((n) * sizeof(int)))
#define newveck(n)     (checksize(n), newvec(n))

/* return sink: `return (guard, value);` */
static int *make(int n) { return (checksize(n), newvec(n)); }

int main(void) {
    int *p = NULL;
    p = newveck(4);                 /* assignment sink: guard runs (checks=1), p=malloc */
    p[0] = 11; p[3] = 44;
    printf("p: %d %d  checks=%d\n", p[0], p[3], checks);   /* 11 44, checks=1 */
    free(p);

    int *q = make(2);               /* return sink inside make(): checks=2 */
    q[0] = 7;
    printf("q: %d  checks=%d\n", q[0], checks);            /* 7, checks=2 */
    free(q);
    return 0;
}
