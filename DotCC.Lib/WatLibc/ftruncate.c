/* ftruncate: make the file open as fd length bytes long (POSIX), through WASI's
   fd_filestat_set_size. The wat target's libc (WatLibc), compiled with the program. */
#include <errno.h>
#include <unistd.h>
#include <posix_impl.h>

int ftruncate(int fd, off_t length)
{
    int err;
    if (length < 0)
    {
        errno = EINVAL;
        return -1;
    }
    err = __wasi_fd_filestat_set_size(fd, (unsigned long)length);
    return err ? __wasi_fail(err) : 0;
}
