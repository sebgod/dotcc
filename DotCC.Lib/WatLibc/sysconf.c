/* sysconf: a configuration value (POSIX): 100 clock ticks a second (times), a page of 64 KiB (a
   wasm page), and one processor; any other name fails EINVAL. The wat target's libc (WatLibc), compiled with the program. */
#include <errno.h>
#include <unistd.h>

long sysconf(int name)
{
    switch (name)
    {
    case _SC_CLK_TCK: return 100;
    case _SC_PAGESIZE: return 65536;
    case _SC_NPROCESSORS_CONF:
    case _SC_NPROCESSORS_ONLN: return 1;
    default:
        errno = EINVAL;
        return -1;
    }
}
