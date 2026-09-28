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

/* Resource limits (POSIX getrlimit / setrlimit), with Linux's resource
   numbers. On Linux both forward to libc. Elsewhere no such per-process limit
   exists, so getrlimit reports every limit as RLIM_INFINITY and setrlimit
   fails EPERM: a limit cannot be imposed. */
typedef unsigned long rlim_t;
#define RLIM_INFINITY  (~0UL)
#define RLIMIT_CPU     0
#define RLIMIT_FSIZE   1
#define RLIMIT_DATA    2
#define RLIMIT_STACK   3
#define RLIMIT_CORE    4
#define RLIMIT_RSS     5
#define RLIMIT_NPROC   6
#define RLIMIT_NOFILE  7
#define RLIMIT_MEMLOCK 8
#define RLIMIT_AS      9

struct rlimit {
    rlim_t rlim_cur;   /* the soft limit */
    rlim_t rlim_max;   /* the hard limit */
};

int getrlimit(int resource, struct rlimit *rlim);
int setrlimit(int resource, const struct rlimit *rlim);

#endif /* _SYS_RESOURCE_H */
