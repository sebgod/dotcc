/* select: wait until a descriptor is ready, or the timeout (POSIX). dotcc's fd_set is a stub
   (<unistd.h>), so select waits only on the clock: with no descriptor to watch (nfds 0, or no
   set) it sleeps for timeout and returns 0, and asked about one it fails ENOSYS. A wait with
   neither would never end, and fails EINVAL. The wat target's libc (WatLibc), compiled with the program. */
#include <errno.h>
#include <unistd.h>
#include <posix_impl.h>

int select(int nfds, fd_set *readfds, fd_set *writefds, fd_set *errorfds, struct timeval *timeout)
{
    int err;
    if (nfds > 0 && (readfds || writefds || errorfds))
    {
        errno = ENOSYS;
        return -1;
    }
    if (!timeout || timeout->tv_sec < 0 || timeout->tv_usec < 0)
    {
        errno = EINVAL;
        return -1;
    }
    err = __wasi_sleep((unsigned long)timeout->tv_sec * 1000000000UL + (unsigned long)timeout->tv_usec * 1000UL);
    return err ? __wasi_fail(err) : 0;
}
