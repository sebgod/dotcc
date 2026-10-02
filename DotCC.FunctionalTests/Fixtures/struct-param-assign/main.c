/* A struct parameter is the callee's own copy: assigning another struct to it copies into that
   copy, and changing it afterwards changes neither the argument nor the struct assigned from
   (CPython's merge_lo: `ssa = ms->a;` then advances ssa.keys). */
#include <stdio.h>

typedef struct { int *keys; int *values; } slice;
struct state { long n; slice a; int arr[4]; };

static void advance(slice *s, int n) { s->keys += n; }

static int merge(struct state *ms, slice ssa, int na)
{
    slice dest = ssa;
    ssa = ms->a;
    advance(&ssa, na);
    *dest.keys = 7;
    return (int)(ssa.keys - ms->arr);
}

int main(void)
{
    struct state ms;
    int other[4] = { 0 };
    slice s = { other, 0 };
    ms.a.keys = ms.arr;
    ms.a.values = 0;
    int moved = merge(&ms, s, 3);
    printf("moved=%d a.keys==arr %d s.keys==other %d other[0]=%d\n", moved, ms.a.keys == ms.arr, s.keys == other, other[0]);
    return 0;
}
