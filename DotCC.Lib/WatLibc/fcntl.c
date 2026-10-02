/* fcntl: a file descriptor's flags (POSIX). F_GETFL and F_SETFL are WASI's fd_fdstat_get and
   fd_fdstat_set_flags (O_APPEND and O_NONBLOCK; the access mode from the descriptor's rights).
   A program here runs no other, so an fd is never inherited: F_GETFD says FD_CLOEXEC and
   F_SETFD changes nothing, as wasi-libc's do. The wat target's libc (WatLibc), compiled with the program. */
#include <errno.h>
#include <fcntl.h>
#include <stdarg.h>
#include <posix_impl.h>

int fcntl(int fd, int cmd, ...)
{
    struct __wasi_fdstat st;
    int err = __wasi_fd_fdstat_get(fd, &st);
    if (err)
    {
        return __wasi_fail(err);
    }
    switch (cmd)
    {
    case F_GETFD:
        return FD_CLOEXEC;
    case F_SETFD:
        return 0;
    case F_GETFL:
    {
        int flags = 0;
        int reads = (st.fs_rights_base & __WASI_RIGHTS_FD_READ) != 0;
        int writes = (st.fs_rights_base & __WASI_RIGHTS_FD_WRITE) != 0;
        if (reads && writes) { flags = O_RDWR; }
        else if (writes) { flags = O_WRONLY; }
        if (st.fs_flags & __WASI_FDFLAGS_APPEND) { flags |= O_APPEND; }
        if (st.fs_flags & __WASI_FDFLAGS_NONBLOCK) { flags |= O_NONBLOCK; }
        return flags;
    }
    case F_SETFL:
    {
        va_list ap;
        int flags;
        unsigned fdflags = st.fs_flags & ~(unsigned)(__WASI_FDFLAGS_APPEND | __WASI_FDFLAGS_NONBLOCK);
        va_start(ap, cmd);
        flags = va_arg(ap, int);
        va_end(ap);
        if (flags & O_APPEND) { fdflags |= __WASI_FDFLAGS_APPEND; }
        if (flags & O_NONBLOCK) { fdflags |= __WASI_FDFLAGS_NONBLOCK; }
        err = __wasi_fd_fdstat_set_flags(fd, fdflags);
        return err ? __wasi_fail(err) : 0;
    }
    default:
        errno = EINVAL;
        return -1;
    }
}
