/* sizeof(struct) regression: struct/union sizes tracked from member fields.
   Verifies that sizeof(T) * constant expressions are preserved. */
#include <stdio.h>

typedef union Value { void *p; double n; int b; long long i; } Value;
typedef unsigned char lu_byte;
typedef struct TValue { Value value_; lu_byte tt_; } TValue;

int main(void) {
    printf("sizeof(Value)=%d\n", (int)sizeof(Value));
    printf("sizeof(TValue)=%d\n", (int)sizeof(TValue));
    printf("sizeof(TValue)*2=%d\n", (int)(sizeof(TValue) * 2));
    printf("sizeof(Value)+sizeof(lu_byte)=%d\n", (int)(sizeof(Value) + sizeof(lu_byte)));
    return 0;
}
