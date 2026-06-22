/* Milestone ß ("sharp-s") — the GCC `__int128` / `unsigned __int128` extension, lowered to C#
 * System.Int128 / System.UInt128. The values genuinely exceed 64 bits (2^80 via an unsigned
 * __int128 multiply; -(2^100) as a signed __int128 with a sign-preserving arithmetic shift), so
 * a 64-bit lowering would truncate them. Reduced to observable 64-bit values for stdout. */
#include <stdio.h>

int main(void) {
    unsigned __int128 a = (unsigned __int128)1 << 40;
    unsigned __int128 wide = a * a;                            /* 2^80 */
    unsigned long long hi = (unsigned long long)(wide >> 64);  /* 2^16 = 65536 */

    __int128 big = -((__int128)1 << 100);
    long long neg = (long long)(big >> 100);                   /* -1 */

    printf("hi=%llu neg=%lld size=%d\n", hi, neg, (int)sizeof(__int128));
    return 0;
}
