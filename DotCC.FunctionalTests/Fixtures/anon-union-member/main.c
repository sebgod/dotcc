#include <stdio.h>

/* C11 anonymous union member — the tagged-union staple. Fields i/d overlap and
   are promoted into Value (written `v.i`, not `v.u.i`). dotcc lifts them into a
   generated nested [StructLayout(Explicit)] type and rewrites the access. The
   tag selects the active member; `extra` after the union is a normal field. */

struct Value {
    int tag;
    union { int i; double d; };
    int extra;
};

void show(struct Value *v) {
    if (v->tag == 0) printf("int %d\n", v->i);
    else             printf("dbl %.1f\n", v->d);
}

int main(void) {
    struct Value a;
    a.tag = 0; a.i = 42; a.extra = 7;
    struct Value b;
    b.tag = 1; b.d = 3.5; b.extra = 9;

    printf("%d %d %d\n", a.tag, a.i, a.extra);
    show(&a);
    show(&b);
    printf("%d\n", b.extra);
    return 0;
}
