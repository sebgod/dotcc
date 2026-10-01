/* asctime_r: *t as "Www Mmm dd hh:mm:ss yyyy\n" in the C locale, into buf (26 bytes at least),
   the form 7.27.3.1 gives as "%.3s %.3s%3d %.2d:%.2d:%.2d %d\n". The wat target's libc
   (WatLibc), compiled with the program. */
#include <__tm.h>
#include <stdio.h>
#include <stddef.h>

char *asctime_r(struct tm *t, char *buf)
{
    if (t == NULL || buf == NULL) { return NULL; }
    sprintf(buf, "%.3s %.3s%3d %.2d:%.2d:%.2d %d\n",
        __tm_names.wday_abbr[(t->tm_wday % 7 + 7) % 7], __tm_names.mon_abbr[(t->tm_mon % 12 + 12) % 12],
        t->tm_mday, t->tm_hour, t->tm_min, t->tm_sec, 1900 + t->tm_year);
    return buf;
}
