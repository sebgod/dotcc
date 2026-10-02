/* access: whether path exists (POSIX). WASI has no permissions, so a file that exists may be
   read, written and run as far as access can tell; the call that tries says otherwise.
   The wat target's libc (WatLibc), compiled with the program. */
#include <errno.h>
#include <string.h>
#include <unistd.h>
#include <posix_impl.h>

int access(const char *path, int mode)
{
    char rel[__DOTCC_PATH_MAX];
    struct __wasi_filestat fs;
    int err;
    int dirfd;
    if (mode & ~(R_OK | W_OK | X_OK))
    {
        errno = EINVAL;
        return -1;
    }
    dirfd = __wasi_resolve(path, rel, sizeof rel);
    if (dirfd < 0)
    {
        return -1;
    }
    err = __wasi_path_filestat_get(dirfd, __WASI_LOOKUP_SYMLINK_FOLLOW, rel, strlen(rel), &fs);
    return err ? __wasi_fail(err) : 0;
}
