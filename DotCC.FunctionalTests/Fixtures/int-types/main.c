// Extended integer type family: signed/unsigned + short/long/long-long
// combinations, plus integer literal suffixes (u, L, UL, etc.). MSVC oracle
// confirms each lands at byte-identical output.

#include "stdio.h"

int main() {
    // Plain forms.
    short s = 100;
    unsigned short us = 60000;
    long l = 2000000000L;
    unsigned long ul = 4000000000UL;
    long long ll = 9000000000LL;
    unsigned long long ull = 18000000000ULL;
    unsigned u = 12345u;
    signed int si = -42;

    printf("short=%d unsigned short=%d\n", s, us);
    printf("long=%ld unsigned long=%lu\n", l, ul);
    printf("long long=%lld unsigned long long=%llu\n", ll, ull);
    printf("unsigned=%u signed int=%d\n", u, si);

    // Free-order specifiers — the real-compiler-shape grammar accepts any
    // order of {signed/unsigned, short/long, int}. All these produce the
    // same C# type and the same printf output.
    long int x = 42L;
    int long y = 43L;
    long signed z = 44L;
    printf("free order: %ld %ld %ld\n", x, y, z);

    unsigned long long u1 = 100ULL;
    long long unsigned u2 = 101ULL;
    long unsigned long u3 = 102ULL;
    printf("free unsigned: %llu %llu %llu\n", u1, u2, u3);

    // signed/unsigned char map to sbyte/byte.
    unsigned char uc = 200;
    signed char sc = -50;
    printf("uchar=%d schar=%d\n", uc, sc);

    return 0;
}
