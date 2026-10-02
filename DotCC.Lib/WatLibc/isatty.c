/* isatty: whether fd is a terminal (POSIX): a character device that neither seeks nor tells,
   as wasi-libc's isatty tells one. The wat target's libc (WatLibc), compiled with the program. */
#include <errno.h>
#include <unistd.h>
#include <posix_impl.h>

int isatty(int fd)
{
    struct __wasi_fdstat st;
    int err = __wasi_fd_fdstat_get(fd, &st);
    if (err)
    {
        errno = __wasi_errno(err);
        return 0;
    }
    if (st.fs_filetype != __WASI_FILETYPE_CHARACTER_DEVICE
        || (st.fs_rights_base & (__WASI_RIGHTS_FD_SEEK | __WASI_RIGHTS_FD_TELL)))
    {
        errno = ENOTTY;
        return 0;
    }
    return 1;
}
