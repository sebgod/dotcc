#ifndef _SYS_RESOURCE_H
#define _SYS_RESOURCE_H

/* dotcc's <sys/resource.h> — just the getrusage() surface chibi's (chibi time)
   touches. dotcc has no portable per-process CPU accounting, so getrusage()
   zeroes the struct and returns success (best-effort, like chmod): any CPU-time
   delta a caller computes is 0, which is harmless for the R7RS time tests that
   use wall-clock (gettimeofday) rather than CPU time. */

#include <sys/time.h>   /* struct timeval */

#define RUSAGE_SELF     0
#define RUSAGE_CHILDREN (-1)

struct rusage {
    struct timeval ru_utime;   /* user CPU time */
    struct timeval ru_stime;   /* system CPU time */
    long ru_maxrss;
    long ru_ixrss;
    long ru_idrss;
    long ru_isrss;
    long ru_minflt;
    long ru_majflt;
    long ru_nswap;
    long ru_inblock;
    long ru_oublock;
    long ru_msgsnd;
    long ru_msgrcv;
    long ru_nsignals;
    long ru_nvcsw;
    long ru_nivcsw;
};

int getrusage(int who, struct rusage *usage);

#endif /* _SYS_RESOURCE_H */
