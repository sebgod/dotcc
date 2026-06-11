#ifndef _SIGNAL_H
#define _SIGNAL_H

/* dotcc's <signal.h> (C90 7.14) — header-only for now. The CORE/standard-library
   translation units only need `sig_atomic_t` (e.g. Lua's `volatile sig_atomic_t
   trap;` interrupt flag); the `signal()` / `raise()` FUNCTIONS are used only by the
   standalone REPL (lua.c), which is deferred. When those land they'll be backed by
   .NET's System.Runtime.InteropServices.PosixSignalRegistration (cross-platform,
   the SIGINT/SIGTERM/SIGQUIT/SIGHUP "terminal" signals) — the handler-sets-a-
   volatile-flag idiom that maps onto the trap mechanism dotcc already supports.
   The fault signals (SIGSEGV/SIGFPE/SIGILL/SIGABRT) can't be portably handled on
   .NET (the runtime owns them); they're defined here for compilation only. */

/* Type for an object that can be accessed atomically from a signal handler. */
typedef int sig_atomic_t;

/* Special handler values (C90). Cast to the handler type per the standard. */
#define SIG_DFL ((void (*)(int))0)
#define SIG_IGN ((void (*)(int))1)
#define SIG_ERR ((void (*)(int))-1)

/* C standard: void (*signal(int sig, void (*func)(int)))(int); */
void (*signal(int sig, void (*func)(int)))(int);

/* The six C-standard signal numbers (POSIX values). */
#define SIGINT  2
#define SIGILL  4
#define SIGABRT 6
#define SIGFPE  8
#define SIGSEGV 11
#define SIGTERM 15

/* POSIX signal-set + sigaction surface (chibi's (chibi process)). dotcc can't
   deliver POSIX signals on .NET — the runtime owns them — so sigaction /
   sigprocmask / kill / raise are stubs that compile + load (a handler never
   fires); the sigset_t manipulators are real bitset ops (DotCC.Libc's
   ProcessSignalLib). Enough surface that (chibi process) parses, type-checks,
   and loads; the R7RS suite drives command-line/exit, not signals. */
typedef unsigned long sigset_t;

typedef struct {
    int   si_signo;
    int   si_errno;
    int   si_code;
    int   si_pid;
    int   si_uid;
    int   si_status;
    void *si_addr;
} siginfo_t;

struct sigaction {
    void (*sa_handler)(int);
    void (*sa_sigaction)(int, siginfo_t *, void *);
    sigset_t sa_mask;
    int sa_flags;
};

/* Further POSIX signal numbers (Linux values). */
#define SIGHUP   1
#define SIGQUIT  3
#define SIGKILL  9
#define SIGUSR1  10
#define SIGUSR2  12
#define SIGPIPE  13
#define SIGALRM  14
#define SIGCHLD  17
#define SIGCONT  18
#define SIGSTOP  19
#define SIGTSTP  20
#define SIGTTIN  21
#define SIGTTOU  22

/* sa_flags bits + sigprocmask() 'how' values. */
#define SA_SIGINFO  0x00000004
#define SA_RESTART  0x10000000
#define SA_NODEFER  0x40000000
#define SIG_BLOCK   0
#define SIG_UNBLOCK 1
#define SIG_SETMASK 2

int sigaction(int sig, const struct sigaction *act, struct sigaction *oldact);
int sigprocmask(int how, const sigset_t *set, sigset_t *oldset);
int sigemptyset(sigset_t *set);
int sigfillset(sigset_t *set);
int sigaddset(sigset_t *set, int signum);
int sigdelset(sigset_t *set, int signum);
int sigismember(const sigset_t *set, int signum);
int kill(int pid, int sig);
int raise(int sig);

#endif
