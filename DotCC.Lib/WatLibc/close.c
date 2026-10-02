/* close: close file descriptor fd (POSIX), through WASI's fd_close. The wat target's libc (WatLibc), compiled with the program. */
#include <unistd.h>
#include <posix_impl.h>

int close(int fd)
{
    int err = __wasi_fd_close(fd);
    return err ? __wasi_fail(err) : 0;
}
