#ifndef _UNISTD_H
#define _UNISTD_H

/* dotcc's <unistd.h> — a minimal POSIX surface, present so portable Unix C
   (chibi-scheme's non-_WIN32 path) PARSES and links against honest .NET
   lowerings. Not a full POSIX claim:

   - usleep / isatty route to the obvious BCL primitives (Thread.Sleep,
     Console.Is*Redirected) in DotCC.Libc.UnistdLib.
   - The select() surface (fd_set, FD_* and struct timeval — glibc exposes
     them transitively from here, which is what portable code relies on) is
     declared so poll-style code COMPILES; calling select() at runtime throws
     NotSupportedException (fail loudly — .NET has no fd-level select). The
     FD_* manipulators are no-op functions (not macros, so no statement-form
     expansion edge cases); their only consumer is the select() that throws. */

typedef long fd_set;

struct timeval {
    long tv_sec;
    long tv_usec;
};

void FD_ZERO(fd_set *set);
void FD_SET(int fd, fd_set *set);
void FD_CLR(int fd, fd_set *set);
int  FD_ISSET(int fd, fd_set *set);

int select(int nfds, fd_set *readfds, fd_set *writefds, fd_set *errorfds, struct timeval *timeout);

int usleep(unsigned int usec);
int isatty(int fd);

#endif /* _UNISTD_H */
