/* _exit: end the program with status at once, no atexit functions and no flush (POSIX), as
   _Exit does: WASI's proc_exit. The wat target's libc (WatLibc), compiled with the program. */
#include <stdlib.h>
#include <unistd.h>

void _exit(int status)
{
    _Exit(status);
}
