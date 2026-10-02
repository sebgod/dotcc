/* lseek: move file descriptor fd's offset (POSIX), through WASI's fd_seek, whose whence values
   are SEEK_SET, SEEK_CUR and SEEK_END's. The wat target's libc (WatLibc), compiled with the program. */
#include <unistd.h>
#include <posix_impl.h>

off_t lseek(int fd, off_t offset, int whence)
{
    unsigned long at;
    int err = __wasi_fd_seek(fd, offset, whence, &at);
    if (err)
    {
        return __wasi_fail(err);
    }
    return (off_t)at;
}
