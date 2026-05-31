/* <time.h> calendar family: break a fixed Unix timestamp down with gmtime
   (UTC, so the output is machine/timezone-independent and both oracles agree),
   then format it with strftime and asctime. Drives the `struct tm` lowering
   end-to-end through the emitter (FILE-free: `struct tm *` is a real pointer,
   field access via `->`). */
#include <stdio.h>
#include <time.h>

int main(void) {
    time_t t = 1700000000; /* 2023-11-14 22:13:20 UTC, a Tuesday */
    struct tm *g = gmtime(&t);

    printf("%04d-%02d-%02d %02d:%02d:%02d\n",
        g->tm_year + 1900, g->tm_mon + 1, g->tm_mday,
        g->tm_hour, g->tm_min, g->tm_sec);
    printf("wday=%d yday=%d\n", g->tm_wday, g->tm_yday);

    char buf[64];
    strftime(buf, sizeof(buf), "%A %B %d, %Y", g);
    printf("%s\n", buf);

    printf("%s", asctime(g));
    return 0;
}
