#ifndef _TIME_H
#define _TIME_H

/* dotcc's <time.h> — time surface (DotCC.Libc/TimeLib.cs scalar bits +
   CalendarLib.cs struct tm family). time_t / clock_t lower to C# long
   (dotcc's LP64 model). clock() is a monotonic millisecond counter
   (Environment.TickCount64), so CLOCKS_PER_SEC is 1000 and
   (clock()-start)/(double)CLOCKS_PER_SEC gives elapsed wall-clock seconds.

   `struct tm` is the runtime Libc.tm struct. dotcc parses `struct tm` via
   the usual `struct ID` rule (typeStruct) and emits the bare tag `tm`, which
   resolves to Libc.tm through `using static Libc;`. The body is therefore
   declared ONLY in the runtime, NOT here: a `struct tm { … };` in this header
   would make the emitter emit a second, top-level `tm` that collides with
   Libc.tm. (And `tm` must NOT be seeded as a type name — that would break the
   `struct ID` parse.) The fields, for reference, are the C89 set: tm_sec,
   tm_min, tm_hour, tm_mday, tm_mon (0-11), tm_year (since 1900), tm_wday
   (0-6, Sun=0), tm_yday (0-365), tm_isdst. */

#ifndef NULL
#define NULL null
#endif

typedef long time_t;
typedef long clock_t;

#define CLOCKS_PER_SEC 1000

time_t time(time_t* t);
clock_t clock(void);
double difftime(time_t end, time_t beginning);

/* Calendar conversions. gmtime/localtime/asctime/ctime return a pointer
   into a reused static buffer (overwritten by the next call). */
struct tm* gmtime(time_t* timer);
struct tm* localtime(time_t* timer);
time_t mktime(struct tm* t);
char* asctime(struct tm* t);
char* ctime(time_t* timer);
int strftime(char* s, int max, char* fmt, struct tm* t);

#endif
