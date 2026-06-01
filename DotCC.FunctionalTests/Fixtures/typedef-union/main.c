#include <stdio.h>

/* `typedef union Tag { … } Alias;` and the anonymous form — the union
 * counterparts of typedef-struct. Lua's core value representation is exactly
 * this shape (`typedef union Value { … } Value;` in lobject.h). Members share
 * storage (overlap), so writing one field reads back through it. */

typedef union Value { int i; float f; unsigned char bytes[4]; } Value;  /* tagged */
typedef union { long n; double d; } Num;                                /* anonymous */

int main(void) {
    Value v;
    v.i = 0;
    v.f = 1.5f;                 /* overwrites the same storage as .i */
    Num m;
    m.d = 2.25;
    printf("f=%g bytes0=%d d=%g szV=%d szN=%d\n",
           v.f, (int)v.bytes[0], m.d, (int)sizeof(Value), (int)sizeof(Num));
    return 0;
}
