/* open: open path as a new file descriptor (POSIX), through WASI's path_open; the descriptor
   reads, writes or both as flags' access mode says. The mode a created file would get is not
   asked for, as WASI has no permissions. The wat target's libc (WatLibc), compiled with the program. */
#include <fcntl.h>
#include <string.h>
#include <posix_impl.h>

int open(const char *path, int flags, ...)
{
    char rel[__DOTCC_PATH_MAX];
    unsigned oflags = 0;
    unsigned fdflags = 0;
    unsigned long rights = __WASI_RIGHTS_ALL;
    int fd;
    int err;
    int dirfd = __wasi_resolve(path, rel, sizeof rel);
    if (dirfd < 0)
    {
        return -1;
    }
    if (flags & O_CREAT) { oflags |= __WASI_OFLAGS_CREAT; }
    if (flags & O_DIRECTORY) { oflags |= __WASI_OFLAGS_DIRECTORY; }
    if (flags & O_EXCL) { oflags |= __WASI_OFLAGS_EXCL; }
    if (flags & O_TRUNC) { oflags |= __WASI_OFLAGS_TRUNC; }
    if (flags & O_APPEND) { fdflags |= __WASI_FDFLAGS_APPEND; }
    if (flags & O_NONBLOCK) { fdflags |= __WASI_FDFLAGS_NONBLOCK; }
    switch (flags & O_ACCMODE)
    {
    case O_RDONLY: rights &= ~__WASI_RIGHTS_FD_WRITE; break;
    case O_WRONLY: rights &= ~__WASI_RIGHTS_FD_READ; break;
    default: break;
    }
    err = __wasi_path_open(dirfd, (flags & O_NOFOLLOW) ? 0 : __WASI_LOOKUP_SYMLINK_FOLLOW, rel, strlen(rel),
                           oflags, rights, __WASI_RIGHTS_ALL, fdflags, &fd);
    if (err)
    {
        return __wasi_fail(err);
    }
    return fd;
}
