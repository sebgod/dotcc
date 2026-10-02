/* stat: path's attributes (POSIX), a symbolic link's target's, through WASI's path_filestat_get. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>
#include <sys/stat.h>
#include <posix_impl.h>

int stat(const char *path, struct stat *buf)
{
    char rel[__DOTCC_PATH_MAX];
    struct __wasi_filestat fs;
    int err;
    int dirfd = __wasi_resolve(path, rel, sizeof rel);
    if (dirfd < 0)
    {
        return -1;
    }
    err = __wasi_path_filestat_get(dirfd, __WASI_LOOKUP_SYMLINK_FOLLOW, rel, strlen(rel), &fs);
    if (err)
    {
        return __wasi_fail(err);
    }
    __wasi_to_stat(&fs, buf);
    return 0;
}
