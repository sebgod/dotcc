#ifndef _DOTCC_TM_H
#define _DOTCC_TM_H

/* What the wat target's libc's calendar functions share: the C locale's day and month names
   (one data object, __tm_names, a unit of its own) and the day count of a civil date. The
   functions mirror the C# runtime's (DotCC.Libc/CalendarLib.cs): UTC, as WASI has no time
   zone, so localtime is gmtime and tm_zone is "UTC". */

#include <time.h>

extern const struct __tm_names_t {
    const char *wday_abbr[7];
    const char *wday_full[7];
    const char *mon_abbr[12];
    const char *mon_full[12];
} __tm_names;

/* Days from 1970-01-01 to year-month-day (month 1-12) in the proleptic Gregorian calendar. */
long __days_from_civil(long year, int month, int day);

#endif
