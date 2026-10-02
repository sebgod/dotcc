#ifndef _FCNTL_H
#define _FCNTL_H

/* dotcc's <fcntl.h> — the fd-flag surface portable Unix C touches (chibi's
   nonblocking-port probe). fcntl() is honest about what .NET can do: F_GETFL
   reports no flags set and F_SETFL claims success WITHOUT making the fd
   nonblocking (DotCC.Libc has no fd-level nonblocking I/O) — so a
   `getc`-after-O_NONBLOCK readiness probe degrades to a BLOCKING read. For
   file-backed streams the two behave identically (a file read never blocks);
   only an interactive/pipe probe would diverge. */

#define F_GETFD 1
#define F_SETFD 2
#define F_GETFL 3
#define F_SETFL 4

/* The one fd flag (F_GETFD / F_SETFD). F_GETFD reports it clear and F_SETFD
   claims success: dotcc runs no child processes, so an fd is never inherited
   either way. */
#define FD_CLOEXEC 1

#define O_RDONLY   0x0
#define O_WRONLY   0x1
#define O_RDWR     0x2
#define O_ACCMODE  0x3
#define O_CREAT    0x40
#define O_EXCL     0x80
#define O_TRUNC    0x200
#define O_APPEND   0x400
#define O_NONBLOCK 0x800
/* Linux's values. open() has nothing to do for O_NOCTTY (dotcc has no
   controlling terminal) or O_CLOEXEC (it runs no child to inherit the fd).
   O_NOFOLLOW is accepted but not enforced: a symbolic link as the last
   component is followed, as .NET's file open does. */
#define O_NOCTTY    0x100
#define O_NOFOLLOW  0x20000
#define O_CLOEXEC   0x80000
/* Linux's value: open fails ENOTDIR unless path is a directory (the wat target, where opendir
   opens one; the C# runtime's open does not check it). */
#define O_DIRECTORY 0x10000

int fcntl(int fd, int cmd, ...);

/* open() returns a dotcc fd (a FileSlot index, same space as fileno); the
   O_* access/creation flags above map to .NET FileMode/FileAccess in
   DotCC.Libc.PosixFsLib. The mode argument is accepted (best-effort chmod). */
int open(const char *path, int flags, ...);

#endif /* _FCNTL_H */
