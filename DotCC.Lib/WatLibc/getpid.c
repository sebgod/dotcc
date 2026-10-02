/* getpid: the program's process id (POSIX). WASI has no processes: the program is the only one
   it sees, so its id is 1. The wat target's libc (WatLibc), compiled with the program. */
#include <unistd.h>

int getpid(void)
{
    return 1;
}
