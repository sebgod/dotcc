/* read: up to count bytes from the file descriptor fd into buf, through WASI's fd_read; the count
   read (0 at the end of the file), or -1 with errno. The wat target's libc (WatLibc), compiled with the program. */
#include <unistd.h>
#include <errno.h>
#include <wasi.h>

long read(int fd, void *buf, unsigned long count)
{
    struct __wasi_iovec iov = { __wasi_addr(buf), (unsigned)count };
    unsigned got;
    int err = __wasi_fd_read(fd, &iov, 1, &got);
    if (err)
    {
        errno = __wasi_errno(err);
        return -1;
    }
    return got;
}
