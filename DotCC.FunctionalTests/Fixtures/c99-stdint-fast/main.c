/* C99 <stdint.h> minimum-width (int_leastN_t) and fastest-minimum-width
 * (int_fastN_t) integer families. dotcc follows glibc's LP64 mapping exactly:
 * the least types are the exact-width types, but fast16/32/64 are all `long`
 * (64-bit), so a value past 2^31 fits and sizeof is 8. Verified vs gcc -std=c17.
 * MSVC opts out — it is LLP64 and maps the fast types to 32-bit int (a
 * deliberate data-model divergence, like the other sizeof fixtures). */
#include <stdio.h>
#include <stdint.h>

int main(void) {
    int_least8_t   la = -100;
    uint_least16_t lb = 60000;
    int_least32_t  lc = -2000000000;

    int_fast16_t   fa = 5000000000;          /* > 2^31: relies on 64-bit fast16 */
    uint_fast32_t  fb = 10000000000u;
    int_fast64_t   fc = fa + (int_fast64_t)fb;

    printf("sizes %zu %zu %zu %zu\n",
           sizeof(int_least8_t), sizeof(int_fast16_t),
           sizeof(uint_fast32_t), sizeof(int_fast64_t));
    printf("least %d %u %d\n", (int)la, (unsigned)lb, (int)lc);
    printf("fast %lld %llu %lld\n",
           (long long)fa, (unsigned long long)fb, (long long)fc);
    printf("maxfast16=%lld\n", (long long)INT_FAST16_MAX);
    return 0;
}
