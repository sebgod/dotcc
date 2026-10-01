/* mktime: the seconds since the Epoch of *t read as local time (UTC here), its fields first
   folded into range (a 13th month is the next year's first), and *t normalized in place with
   tm_wday and tm_yday filled; -1 when out of range. The wat target's libc (WatLibc), compiled
   with the program. */
#include <__tm.h>
#include <stddef.h>

time_t mktime(struct tm *t)
{
    if (t == NULL) { return -1; }
    long year = 1900L + t->tm_year + t->tm_mon / 12;
    int month = t->tm_mon % 12;
    if (month < 0) { month += 12; year--; }
    long days = __days_from_civil(year, month + 1, 1) + t->tm_mday - 1;
    time_t secs = days * 86400 + t->tm_hour * 3600L + t->tm_min * 60L + t->tm_sec;
    if (gmtime_r(&secs, t) == NULL) { return -1; }
    return secs;
}
