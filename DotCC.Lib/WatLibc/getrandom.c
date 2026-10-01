/* getrandom: buflen random bytes from WASI's random_get, the host's secure source, which never
   blocks, so every flag behaves as GRND_NONBLOCK would; -1 with errno EIO when the host fails.
   The wat target's libc (WatLibc), compiled with the program. */
#include <sys/random.h>
#include <errno.h>
#include <wasi.h>

ssize_t getrandom(void *buf, size_t buflen, unsigned int flags)
{
    (void)flags;
    if (__wasi_random_get(buf, (unsigned)buflen) != 0)
    {
        errno = EIO;
        return -1;
    }
    return (ssize_t)buflen;
}
