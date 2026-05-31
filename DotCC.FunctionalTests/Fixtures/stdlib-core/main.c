/* End-to-end coverage for <stdlib.h> integer conversions + arithmetic. All
   ISO C, so both oracles validate. To stay byte-identical across dotcc (LP64,
   64-bit long) and MSVC (LLP64, 32-bit long), `long` values are kept within
   32 bits and anything larger uses `long long` / %lld. */

#include <stdio.h>
#include <stdlib.h>

int main(void)
{
    /* atoi / atol / atoll. */
    printf("atoi = %d\n", atoi("  42abc"));
    printf("atol = %ld\n", atol("-123"));
    printf("atoll = %lld\n", atoll("10000000000"));

    /* strtol with base detection + endptr. */
    char* end;
    long v = strtol("0x1A rest", &end, 0);
    printf("strtol hex = %ld, rest = '%s'\n", v, end);
    printf("strtol oct = %ld\n", strtol("0777", NULL, 0));
    printf("strtol bin = %ld\n", strtol("101", NULL, 2));

    /* strtoul — 0xFFFFFFFF fits a 32-bit unsigned long, so portable. */
    printf("strtoul = %lu\n", strtoul("4294967295", NULL, 10));

    /* abs / labs / llabs. */
    printf("abs = %d, labs = %ld, llabs = %lld\n",
           abs(-5), labs(-100000L), llabs(-10000000000LL));

    /* div / ldiv / lldiv. */
    div_t d = div(17, 5);
    printf("div = %d r %d\n", d.quot, d.rem);
    ldiv_t ld = ldiv(-17L, 5L);
    printf("ldiv = %ld r %ld\n", ld.quot, ld.rem);
    lldiv_t lld = lldiv(100000000000LL, 7LL);
    printf("lldiv = %lld r %lld\n", lld.quot, lld.rem);

    return 0;
}
