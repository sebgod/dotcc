/* <inttypes.h> (C99) — PRI* format macros + greatest-width functions.
   Uses int64/uint32/intptr/intmax which are the same width on dotcc,
   gcc-linux, and MSVC-x64, so both oracles match. Avoids %o (PRIo*),
   which dotcc's printf doesn't format yet. */

#include <stdio.h>
#include <inttypes.h>

int main(void)
{
    int64_t big = 9000000000;
    printf("PRId64 = %" PRId64 "\n", big);

    uint32_t u = 4000000000u;
    printf("PRIu32 = %" PRIu32 "\n", u);

    int32_t hx = 255;
    printf("PRIx32 = %" PRIx32 "\n", hx);

    intptr_t p = 123456789012;
    printf("PRIdPTR = %" PRIdPTR "\n", p);

    intmax_t a = imaxabs(-42);
    printf("imaxabs = %" PRIdMAX "\n", a);

    imaxdiv_t d = imaxdiv(17, 5);
    printf("imaxdiv = %" PRIdMAX " r %" PRIdMAX "\n", d.quot, d.rem);

    intmax_t parsed = strtoimax("123456789", NULL, 10);
    printf("strtoimax = %" PRIdMAX "\n", parsed);

    return 0;
}
