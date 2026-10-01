/* getentropy: length random bytes, at most 256 as POSIX has it (EIO beyond), from WASI's
   random_get. The wat target's libc (WatLibc), compiled with the program. */
#include <sys/random.h>
#include <errno.h>
#include <wasi.h>

int getentropy(void *buffer, size_t length)
{
    if (length > 256 || __wasi_random_get(buffer, (unsigned)length) != 0)
    {
        errno = EIO;
        return -1;
    }
    return 0;
}
