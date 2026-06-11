#ifndef _SYS_FILE_H
#define _SYS_FILE_H

/* dotcc's <sys/file.h> — just the flock() advisory-locking surface portable
   Unix C touches (chibi's (chibi filesystem) file-lock helpers). dotcc has no
   fd-level advisory locks, so flock() is a no-op that returns success: a single
   managed process never contends with itself the way flock() guards against,
   and any cross-process guarantee .NET can't make is one a portable program
   must already tolerate the absence of. The LOCK_* constants are defined so the
   call sites compile. */

#define LOCK_SH 1
#define LOCK_EX 2
#define LOCK_NB 4
#define LOCK_UN 8

int flock(int fd, int operation);

#endif /* _SYS_FILE_H */
