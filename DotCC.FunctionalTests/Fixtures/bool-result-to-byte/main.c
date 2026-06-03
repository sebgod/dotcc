// A comparison or logical (&&/||/!) result is C `int` 0/1. Stored into a narrower
// integer (a lu_byte field/local), C# needs a cast — dotcc tags these results as
// int so the store-conversion layer inserts it (was CS0266 for CBool->byte). Oracled.
#include <stdio.h>

typedef unsigned char lu_byte;
typedef struct { lu_byte a, b, c, d; } Flags;

int main(void) {
    int x = 5, y = 3;
    Flags f;
    f.a = x > y;          // comparison  -> byte field
    f.b = x && y;         // logical &&  -> byte field
    f.c = !y;             // logical !   -> byte field
    f.d = (x > 0) || y;   // logical ||  -> byte field
    lu_byte e = x < y;    // comparison  -> byte local
    printf("%d %d %d %d %d\n", f.a, f.b, f.c, f.d, e);  // 1 1 0 1 0
    return 0;
}
