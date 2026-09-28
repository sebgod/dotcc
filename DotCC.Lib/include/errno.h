#ifndef _ERRNO_H
#define _ERRNO_H

/* dotcc's <errno.h> — C99 7.5. The error indicator `errno` is a
   thread-local int exposed by DotCC.Libc.Libc as a settable property;
   emitted code references the bare name (resolved via `using static
   Libc;`), so no macro is needed here — `errno = 0;` and
   `if (errno == ERANGE)` both work.

   The E* numbers below match the Linux/glibc asm-generic values and are
   mirrored as `const int` in DotCC.Libc/ErrnoLib.cs (which strerror's
   switch keys on) — keep the two in sync. C mandates only EDOM / ERANGE
   / EILSEQ; the rest are the common POSIX set, useful since dotcc maps
   BCL I/O exceptions onto these. */

#define EPERM           1
#define ENOENT          2
#define ESRCH           3
#define EINTR           4
#define EIO             5
#define ENXIO           6
#define E2BIG           7
#define ENOEXEC         8
#define EBADF           9
#define ECHILD          10
#define EAGAIN          11
#define ENOMEM          12
#define EACCES          13
#define EFAULT          14
#define ENOTBLK         15
#define EBUSY           16
#define EEXIST          17
#define EXDEV           18
#define ENODEV          19
#define ENOTDIR         20
#define EISDIR          21
#define EINVAL          22
#define ENFILE          23
#define EMFILE          24
#define ENOTTY          25
#define ETXTBSY         26
#define EFBIG           27
#define ENOSPC          28
#define ESPIPE          29
#define EROFS           30
#define EMLINK          31
#define EPIPE           32
#define EDOM            33
#define ERANGE          34
#define EDEADLK         35
#define ENAMETOOLONG    36
#define ENOLCK          37
#define ENOSYS          38
#define ENOTEMPTY       39
#define ELOOP           40
#define EOVERFLOW       75
#define EILSEQ          84
#define ENOTSOCK        88
#define EMSGSIZE        90
#define EPROTONOSUPPORT 93
#define EOPNOTSUPP      95
#define EAFNOSUPPORT    97
#define EADDRINUSE      98
#define EADDRNOTAVAIL   99
#define ENETDOWN        100
#define ENETUNREACH     101
#define ECONNABORTED    103
#define ECONNRESET      104
#define ENOBUFS         105
#define EISCONN         106
#define ENOTCONN        107
#define ESHUTDOWN       108
#define ETIMEDOUT       110
#define ECONNREFUSED    111
#define EHOSTUNREACH    113
#define EALREADY        114
#define EINPROGRESS     115
#define ECANCELED       125

/* POSIX lets these share a number with another; Linux does. */
#define EWOULDBLOCK     EAGAIN
#define ENOTSUP         EOPNOTSUPP
#define EDEADLOCK       EDEADLK

#endif
