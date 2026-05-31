#ifndef _TIME_H
#define _TIME_H

/* dotcc's <time.h> — scalar time surface (DotCC.Libc/TimeLib.cs).
   time_t / clock_t lower to C# long (dotcc's LP64 model). clock() is a
   monotonic millisecond counter (Environment.TickCount64), so
   CLOCKS_PER_SEC is 1000 and (clock()-start)/(double)CLOCKS_PER_SEC gives
   elapsed wall-clock seconds.

   Not yet provided: the struct tm calendar family (localtime/gmtime/
   mktime/strftime/asctime/ctime) — that needs `struct tm` to resolve to
   a shared runtime type and is tracked in C-SUPPORT.md. */

#ifndef NULL
#define NULL null
#endif

typedef long time_t;
typedef long clock_t;

#define CLOCKS_PER_SEC 1000

time_t time(time_t* t);
clock_t clock(void);
double difftime(time_t end, time_t beginning);

#endif
