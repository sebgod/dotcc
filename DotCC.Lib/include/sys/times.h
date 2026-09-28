#ifndef _SYS_TIMES_H
#define _SYS_TIMES_H

/* dotcc's <sys/times.h> (POSIX): the process's times in clock ticks, of
   which there are sysconf(_SC_CLK_TCK) (100) per second. times() fills the
   user and system CPU time from the runtime's process accounting; the
   children's times are 0, as for a process that has waited for none. It
   returns the ticks elapsed since an arbitrary fixed point (the monotonic
   clock), for differences. */

#include <time.h>   /* clock_t */

struct tms {
    clock_t tms_utime;    /* user CPU time */
    clock_t tms_stime;    /* system CPU time */
    clock_t tms_cutime;   /* user CPU time of waited-for children */
    clock_t tms_cstime;   /* system CPU time of waited-for children */
};

clock_t times(struct tms *buf);

#endif /* _SYS_TIMES_H */
