/* gmtime_r: *timer (seconds since the Epoch) broken down in UTC into *result (Howard Hinnant's
   civil_from_days), or NULL outside years 1 to 9999, as the C# runtime's. The wat target's
   libc (WatLibc), compiled with the program. */
#include <__tm.h>
#include <stddef.h>

struct tm *gmtime_r(time_t *timer, struct tm *result)
{
    if (timer == NULL || result == NULL) { return NULL; }
    long t = *timer;
    long days = t / 86400;
    long secs = t % 86400;
    if (secs < 0) { secs += 86400; days--; }
    long z = days + 719468;
    long era = (z >= 0 ? z : z - 146096) / 146097;
    long doe = z - era * 146097;
    long yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    long doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    long mp = (5 * doy + 2) / 153;
    int mday = (int)(doy - (153 * mp + 2) / 5 + 1);
    int month = (int)(mp < 10 ? mp + 3 : mp - 9);
    long year = yoe + era * 400 + (month <= 2);
    if (year < 1 || year > 9999) { return NULL; }
    result->tm_sec = (int)(secs % 60);
    result->tm_min = (int)(secs / 60 % 60);
    result->tm_hour = (int)(secs / 3600);
    result->tm_mday = mday;
    result->tm_mon = month - 1;
    result->tm_year = (int)(year - 1900);
    result->tm_wday = (int)(((days + 4) % 7 + 7) % 7);   /* 1970-01-01 was a Thursday */
    result->tm_yday = (int)(days - __days_from_civil(year, 1, 1));
    result->tm_isdst = 0;
    result->tm_gmtoff = 0;
    result->tm_zone = "UTC";
    return result;
}
