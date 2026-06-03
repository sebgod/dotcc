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

#endif
