/* Bit-field declarator lists (GH #241, C11 6.7.2.1): several bit-fields in one
   member declaration, mixed with ordinary and pointer declarators, and unnamed
   padding inside a list. CPython's _ctypes_test.c declares
   `signed int A: 1, B:2, C:3, D:2;`. */
#include <stdio.h>

struct Flags {
    signed int A: 1, B: 2, C: 3, D: 2;
};

struct Mixed {
    unsigned int lo: 4, : 4, hi: 4;
    int count, *where;
};

int main(void) {
    struct Flags f = { -1, 1, -3, 1 };
    printf("%d %d %d %d %zu\n", f.A, f.B, f.C, f.D, sizeof(struct Flags));
    f.B = -2;
    f.C = 3;
    printf("%d %d\n", f.B, f.C);

    int n = 7;
    struct Mixed m = { 9, 5, 42, &n };
    m.hi += 1;
    printf("%u %u %d %d %zu\n", m.lo, m.hi, m.count, *m.where, sizeof(struct Mixed));
    return 0;
}
