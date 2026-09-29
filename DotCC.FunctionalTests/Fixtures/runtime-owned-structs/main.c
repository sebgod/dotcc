/* The structs the runtime owns (struct tm, struct timespec, div_t, ldiv_t,
   lldiv_t, imaxdiv_t) have a layout in the IR: aggregate initializers,
   offsetof as a constant, sizeof, and member types _Generic can see. */
#include <stdio.h>
#include <stdlib.h>
#include <stddef.h>
#include <string.h>
#include <time.h>
#include <inttypes.h>

static char pad[offsetof(struct timespec, tv_nsec)];

int main(void)
{
    struct tm t = {0};
    t.tm_year = 70;
    t.tm_mday = 1;
    printf("tm: %d %d %d\n", t.tm_sec, t.tm_year, t.tm_mday);

    struct tm copy;
    memset(&copy, 0xff, sizeof copy);
    copy = t;
    printf("copy: %d %d\n", copy.tm_year, copy.tm_isdst);

    struct timespec ts = { .tv_sec = 1, .tv_nsec = 2 };
    printf("timespec: %ld %ld\n", (long)ts.tv_sec, ts.tv_nsec);
    printf("pad: %d\n", (int)sizeof pad);

    div_t d = div(17, 5);
    ldiv_t ld = ldiv(5000000000L, 3);
    lldiv_t lld = { 3, 4 };
    imaxdiv_t im = imaxdiv(9, 4);
    printf("div: %d %d\n", d.quot, d.rem);
    printf("ldiv: %ld %ld\n", ld.quot, ld.rem);
    printf("lldiv: %lld %lld\n", lld.quot, lld.rem);
    printf("imaxdiv: %" PRIdMAX " %" PRIdMAX "\n", im.quot, im.rem);

    printf("generic: %d\n", _Generic(ldiv(10, 2).quot, long: 1, int: 2, default: 3));
    printf("sizes: %d %d %d\n", (int)sizeof(div_t), (int)sizeof(ldiv_t), (int)sizeof(struct timespec));
    return 0;
}
