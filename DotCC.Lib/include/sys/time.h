#ifndef _SYS_TIME_H
#define _SYS_TIME_H

/* dotcc's <sys/time.h> — gettimeofday over DateTimeOffset.UtcNow (microsecond
   field truncated from 100ns ticks). struct timeval shares its guard with
   <unistd.h>, which also exposes it transitively (the glibc behavior portable
   select() code relies on). */

#ifndef _DOTCC_STRUCT_TIMEVAL
#define _DOTCC_STRUCT_TIMEVAL
struct timeval {
    long tv_sec;
    long tv_usec;
};
#endif

int gettimeofday(struct timeval *tv, void *tz);

#endif /* _SYS_TIME_H */
