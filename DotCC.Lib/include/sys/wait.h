#ifndef _SYS_WAIT_H
#define _SYS_WAIT_H

/* dotcc's <sys/wait.h> — the wait()/waitpid() surface chibi's (chibi process)
   touches. dotcc has no child processes (fork is unavailable on .NET), so
   wait()/waitpid() fail (-1); the status-decoding macros are defined so the
   call sites compile. Loads the module; the R7RS suite never reaps a child. */

#define WNOHANG   1
#define WUNTRACED 2

/* glibc status encoding: low 7 bits = signal, 0x7f = stopped, else exit. */
#define WIFEXITED(s)    (((s) & 0x7f) == 0)
#define WEXITSTATUS(s)  (((s) >> 8) & 0xff)
#define WIFSIGNALED(s)  ((((s) & 0x7f) + 1) >> 1 > 0)
#define WTERMSIG(s)     ((s) & 0x7f)
#define WIFSTOPPED(s)   (((s) & 0xff) == 0x7f)
#define WSTOPSIG(s)     WEXITSTATUS(s)

int wait(int *status);
int waitpid(int pid, int *status, int options);

#endif /* _SYS_WAIT_H */
