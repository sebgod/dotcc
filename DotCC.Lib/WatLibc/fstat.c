/* fstat: the attributes of the file open as fd (POSIX), through WASI's fd_filestat_get.
   The wat target's libc (WatLibc), compiled with the program. */
#include <sys/stat.h>
#include <posix_impl.h>

int fstat(int fd, struct stat *buf)
{
    struct __wasi_filestat fs;
    int err = __wasi_fd_filestat_get(fd, &fs);
    if (err)
    {
        return __wasi_fail(err);
    }
    __wasi_to_stat(&fs, buf);
    return 0;
}
