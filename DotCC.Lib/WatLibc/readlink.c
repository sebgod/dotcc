/* readlink: the target of symbolic link path into buf, at most bufsiz bytes and no NUL (POSIX),
   through WASI's path_readlink; the count. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>
#include <unistd.h>
#include <posix_impl.h>

long readlink(const char *path, char *buf, unsigned long bufsiz)
{
    char rel[__DOTCC_PATH_MAX];
    unsigned used;
    int err;
    int dirfd = __wasi_resolve(path, rel, sizeof rel);
    if (dirfd < 0)
    {
        return -1;
    }
    err = __wasi_path_readlink(dirfd, rel, strlen(rel), buf, (unsigned)bufsiz, &used);
    if (err)
    {
        return __wasi_fail(err);
    }
    return used;
}
