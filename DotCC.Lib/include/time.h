#ifndef _TIME_H
#define _TIME_H

/* dotcc's <time.h> — time surface (DotCC.Libc/TimeLib.cs scalar bits +
   CalendarLib.cs struct tm family). time_t / clock_t lower to C# long
   (dotcc's LP64 model). clock() is a monotonic millisecond counter
   (Environment.TickCount64), so CLOCKS_PER_SEC is 1000 and
   (clock()-start)/(double)CLOCKS_PER_SEC gives elapsed wall-clock seconds.

   `struct tm` and `struct timespec` are runtime-owned: the runtime supplies
   them as Libc.tm / Libc.timespec, and the bare tags resolve there through
   `using static Libc;`. The bodies below exist so the IR knows every field's
   type and layout (sizeof, offsetof, initializers); the backend emits no
   C# struct for a runtime-owned body declared in a synthetic header
   (RuntimeTypeNames.IsRuntimeOwnedAggregate). They must match the C# types
   field for field, which RuntimeOwnedAggregateTests checks. (And `tm` must
   NOT be seeded as a type name — that would break the `struct ID` parse.) */

#ifndef NULL
#define NULL ((void *)0)
#endif

typedef long time_t;
typedef long clock_t;

/* Broken-down calendar time: the C89 fields (tm_mon 0-11, tm_year since
   1900, tm_wday 0-6 with Sunday 0, tm_yday 0-365) plus the glibc/BSD
   tm_gmtoff / tm_zone extensions. */
struct tm {
    int tm_sec;
    int tm_min;
    int tm_hour;
    int tm_mday;
    int tm_mon;
    int tm_year;
    int tm_wday;
    int tm_yday;
    int tm_isdst;
    long tm_gmtoff;
    char *tm_zone;
};

#define CLOCKS_PER_SEC 1000

time_t time(time_t* t);
clock_t clock(void);
double difftime(time_t end, time_t beginning);

/* C11 struct timespec + timespec_get (§7.27). Runtime-owned like `struct tm`
   (Libc.timespec). Also used by <threads.h>'s timed calls, which #include
   this header. */
struct timespec {
    time_t tv_sec;
    long tv_nsec;
};
#define TIME_UTC 1
int timespec_get(struct timespec* ts, int base);

/* POSIX clocks (clock_gettime / clock_getres), with Linux's ids.
   CLOCK_REALTIME is UTC since the epoch; CLOCK_MONOTONIC counts from an
   arbitrary fixed point and never steps; CLOCK_PROCESS_CPUTIME_ID is the
   process's user plus system CPU time. A thread's own CPU time is not
   available, so CLOCK_THREAD_CPUTIME_ID fails EINVAL, as any unknown clock
   does. */
typedef int clockid_t;
#define CLOCK_REALTIME           0
#define CLOCK_MONOTONIC          1
#define CLOCK_PROCESS_CPUTIME_ID 2
#define CLOCK_THREAD_CPUTIME_ID  3
int clock_gettime(clockid_t clk, struct timespec* ts);
int clock_getres(clockid_t clk, struct timespec* res);

/* Calendar conversions. gmtime/localtime/asctime/ctime return a pointer
   into a reused static buffer (overwritten by the next call). */
struct tm* gmtime(time_t* timer);
struct tm* localtime(time_t* timer);
time_t mktime(struct tm* t);
char* asctime(struct tm* t);
char* ctime(time_t* timer);
int strftime(char* s, int max, char* fmt, struct tm* t);

/* Reentrant variants (POSIX): the caller owns the output buffer. These are
   the primitives; the plain forms above wrap them with a thread-local buffer.
   asctime_r/ctime_r require buf to hold at least 26 bytes. */
struct tm* gmtime_r(time_t* timer, struct tm* result);
struct tm* localtime_r(time_t* timer, struct tm* result);
char* asctime_r(struct tm* t, char* buf);
char* ctime_r(time_t* timer, char* buf);

#endif
