#ifndef _FCNTL_H
#define _FCNTL_H

/* dotcc's <fcntl.h> — the fd-flag surface portable Unix C touches (chibi's
   nonblocking-port probe). fcntl() is honest about what .NET can do: F_GETFL
   reports no flags set and F_SETFL claims success WITHOUT making the fd
   nonblocking (DotCC.Libc has no fd-level nonblocking I/O) — so a
   `getc`-after-O_NONBLOCK readiness probe degrades to a BLOCKING read. For
   file-backed streams the two behave identically (a file read never blocks);
   only an interactive/pipe probe would diverge. */

#define F_GETFL 3
#define F_SETFL 4

#define O_RDONLY   0x0
#define O_WRONLY   0x1
#define O_RDWR     0x2
#define O_NONBLOCK 0x800

int fcntl(int fd, int cmd, ...);

#endif /* _FCNTL_H */
