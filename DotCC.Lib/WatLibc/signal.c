/* signal: set the handler of signal sig (C11 7.14.1.1). WASI delivers no signals, so a handler
   runs only for a signal the program raises itself (raise, here too), and SIG_DFL does what the
   host would: a signal that ends a program by default ends this one, with status 128 + sig
   (SIGABRT traps, as abort does), and one that is ignored by default (SIGCHLD, SIGCONT,
   SIGURG, SIGWINCH) is ignored. A handler stays set after it runs, as glibc's signal has it.
   The wat target's libc (WatLibc), compiled with the program.
   dotcc-libc: also defines raise */
#include <errno.h>
#include <signal.h>
#include <stdlib.h>

#define NSIG 65
#define SIGURG 23
#define SIGWINCH 28

static void (*handlers[NSIG])(int);

void (*signal(int sig, void (*func)(int)))(int)
{
    void (*old)(int);
    if (sig < 1 || sig >= NSIG || sig == SIGKILL || sig == SIGSTOP)
    {
        errno = EINVAL;
        return SIG_ERR;
    }
    old = handlers[sig];
    handlers[sig] = func;
    return old;
}

int raise(int sig)
{
    void (*h)(int);
    if (sig < 1 || sig >= NSIG)
    {
        errno = EINVAL;
        return -1;
    }
    h = handlers[sig];
    if (h == SIG_IGN)
    {
        return 0;
    }
    if (h == SIG_DFL)
    {
        switch (sig)
        {
        case SIGCHLD:
        case SIGCONT:
        case SIGURG:
        case SIGWINCH:
            return 0;
        case SIGABRT:
            abort();
        default:
            _Exit(128 + sig);
        }
    }
    h(sig);
    return 0;
}
