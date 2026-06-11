#ifndef _UNISTD_H
#define _UNISTD_H

/* dotcc's <unistd.h> — a minimal POSIX surface, present so portable Unix C
   (chibi-scheme's non-_WIN32 path) PARSES and links against honest .NET
   lowerings. Not a full POSIX *conformance* claim (see _POSIX_VERSION below):

   - usleep / isatty route to the obvious BCL primitives (Thread.Sleep,
     Console.Is*Redirected) in DotCC.Libc.UnistdLib.
   - The select() surface (fd_set, FD_* and struct timeval — glibc exposes
     them transitively from here, which is what portable code relies on) is
     declared so poll-style code COMPILES; calling select() at runtime throws
     NotSupportedException (fail loudly — .NET has no fd-level select). The
     FD_* manipulators are no-op functions (not macros, so no statement-form
     expansion edge cases); their only consumer is the select() that throws. */

/* Advertise the POSIX.1-2008 API surface. dotcc provides that surface on EVERY
   target OS — each call routes to the host primitive at runtime (the
   OperatingSystem-switch in DotCC.Libc) or fails loudly where .NET has no
   primitive. Defining _POSIX_VERSION is what lets portable C take its POSIX
   branch (#ifdef _POSIX_VERSION) instead of silently compiling a non-POSIX
   fallback or #error-ing out — so calls dotcc fully supports (kill / getpid /
   opendir / …) are actually reached. It is NOT a conformance guarantee: the
   unsupported corners (fork / exec / pipe / dup) still return EPERM/-1 — see
   C-SUPPORT.md. As on a real system the macro lives in <unistd.h>, so it's
   visible only after this header is included (we don't predefine the _input_
   feature-test macros _POSIX_C_SOURCE / _POSIX_SOURCE — those are the program's
   to set; dotcc's headers declare unconditionally and ignore them). */
#define _POSIX_VERSION 200809L

typedef long fd_set;

#ifndef _OFF_T_DEFINED
#define _OFF_T_DEFINED
typedef long off_t;
#endif

#ifndef _DOTCC_STRUCT_TIMEVAL
#define _DOTCC_STRUCT_TIMEVAL
struct timeval {
    long tv_sec;
    long tv_usec;
};
#endif

void FD_ZERO(fd_set *set);
void FD_SET(int fd, fd_set *set);
void FD_CLR(int fd, fd_set *set);
int  FD_ISSET(int fd, fd_set *set);

int select(int nfds, fd_set *readfds, fd_set *writefds, fd_set *errorfds, struct timeval *timeout);

int usleep(unsigned int usec);
int isatty(int fd);
int close(int fd);
long read(int fd, void *buf, unsigned long count);
long write(int fd, void *buf, unsigned long count);
off_t lseek(int fd, off_t offset, int whence);

/* access(path, mode) mode bits. */
#define F_OK 0
#define X_OK 1
#define W_OK 2
#define R_OK 4

/* Directory + path operations (DotCC.Libc.PosixFsLib over System.IO). */
int chdir(const char *path);
char *getcwd(char *buf, unsigned long size);
int rmdir(const char *path);
int unlink(const char *path);
int link(const char *oldpath, const char *newpath);
int symlink(const char *target, const char *linkpath);
long readlink(const char *path, char *buf, unsigned long bufsiz);
int access(const char *path, int mode);
int chown(const char *path, unsigned int owner, unsigned int group);
int ftruncate(int fd, off_t length);
int dup(int fd);
int dup2(int oldfd, int newfd);
int pipe(int *pipefd);

/* Process control (DotCC.Libc.ProcessSignalLib). getpid/getppid are faithful
   (real OS pids); fork/exec/wait have no .NET primitive and fail. */
int getpid(void);
int getppid(void);
unsigned int sleep(unsigned int seconds);
int fork(void);
int execvp(const char *file, char *const argv[]);
int execv(const char *path, char *const argv[]);
unsigned int alarm(unsigned int seconds);
void _exit(int status);

#endif /* _UNISTD_H */
