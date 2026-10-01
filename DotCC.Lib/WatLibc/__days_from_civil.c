/* __days_from_civil: days from 1970-01-01 to a proleptic Gregorian date (Howard Hinnant's
   days_from_civil). The wat target's libc (WatLibc), compiled with the program. */
#include <__tm.h>

long __days_from_civil(long year, int month, int day)
{
    year -= month <= 2;
    long era = (year >= 0 ? year : year - 399) / 400;
    long yoe = year - era * 400;
    long doy = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1;
    long doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return era * 146097 + doe - 719468;
}
