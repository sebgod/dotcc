/* write: count bytes from buf to the file descriptor fd, through WASI's fd_write; the count
   written, or -1 with errno. The wat target's libc (WatLibc), compiled with the program. */
#include <unistd.h>
#include <errno.h>
#include <wasi.h>

long write(int fd, void *buf, unsigned long count)
{
    struct __wasi_iovec iov = { __wasi_addr(buf), (unsigned)count };
    unsigned written;
    int err = __wasi_fd_write(fd, &iov, 1, &written);
    if (err)
    {
        errno = __wasi_errno(err);
        return -1;
    }
    return written;
}
