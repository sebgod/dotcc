/* strftime: *t formatted per fmt in the C locale into s, the conversions the C# runtime's has
   (DotCC.Libc/CalendarLib.cs): %Y %y %C %m %d %e %H %I %M %S %p %A %a %B %b %h %j %w %u %F %T
   %R %D %n %t %%, an unknown one copied as it is, a trailing % ending the format. The count
   written without the NUL, or 0 when that with the NUL does not fit in max. The wat target's
   libc (WatLibc), compiled with the program. */
#include <__tm.h>
#include <stdio.h>
#include <string.h>
#include <stddef.h>

int strftime(char *s, int max, char *fmt, struct tm *t)
{
    if (s == NULL || fmt == NULL || t == NULL || max <= 0) { return 0; }
    int wd = (t->tm_wday % 7 + 7) % 7;
    int mo = (t->tm_mon % 12 + 12) % 12;
    int year = 1900 + t->tm_year;
    int n = 0;
    char num[32];
    int stop = 0;
    for (char *p = fmt; *p && !stop; p++)
    {
        const char *piece = num;
        char one[3] = { 0, 0, 0 };
        if (*p != '%')
        {
            one[0] = *p;
            piece = one;
        }
        else
        {
            p++;
            switch (*p)
            {
                case 'Y': sprintf(num, "%d", year); break;
                case 'y': sprintf(num, "%02d", year % 100); break;
                case 'C': sprintf(num, "%02d", year / 100); break;
                case 'm': sprintf(num, "%02d", t->tm_mon + 1); break;
                case 'd': sprintf(num, "%02d", t->tm_mday); break;
                case 'e': sprintf(num, "%2d", t->tm_mday); break;
                case 'H': sprintf(num, "%02d", t->tm_hour); break;
                case 'I': sprintf(num, "%02d", t->tm_hour % 12 == 0 ? 12 : t->tm_hour % 12); break;
                case 'M': sprintf(num, "%02d", t->tm_min); break;
                case 'S': sprintf(num, "%02d", t->tm_sec); break;
                case 'p': piece = t->tm_hour < 12 ? "AM" : "PM"; break;
                case 'A': piece = __tm_names.wday_full[wd]; break;
                case 'a': piece = __tm_names.wday_abbr[wd]; break;
                case 'B': piece = __tm_names.mon_full[mo]; break;
                case 'b': case 'h': piece = __tm_names.mon_abbr[mo]; break;
                case 'j': sprintf(num, "%03d", t->tm_yday + 1); break;
                case 'w': sprintf(num, "%d", wd); break;
                case 'u': sprintf(num, "%d", wd == 0 ? 7 : wd); break;
                case 'F': sprintf(num, "%04d-%02d-%02d", year, t->tm_mon + 1, t->tm_mday); break;
                case 'T': sprintf(num, "%02d:%02d:%02d", t->tm_hour, t->tm_min, t->tm_sec); break;
                case 'R': sprintf(num, "%02d:%02d", t->tm_hour, t->tm_min); break;
                case 'D': sprintf(num, "%02d/%02d/%02d", t->tm_mon + 1, t->tm_mday, year % 100); break;
                case 'n': piece = "\n"; break;
                case 't': piece = "\t"; break;
                case '%': piece = "%"; break;
                case 0: stop = 1; piece = ""; p--; break;   /* a trailing %: the format ends */
                default: one[0] = '%'; one[1] = *p; piece = one; break;
            }
        }
        for (const char *q = piece; *q; q++)
        {
            if (n + 1 >= max) { return 0; }
            s[n++] = *q;
        }
    }
    s[n] = 0;
    return n;
}
