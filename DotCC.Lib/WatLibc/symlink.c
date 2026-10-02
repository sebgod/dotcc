/* symlink: make linkpath a symbolic link to target (POSIX), through WASI's path_symlink; target
   is kept as it is written, not resolved. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>
#include <unistd.h>
#include <posix_impl.h>

int symlink(const char *target, const char *linkpath)
{
    char rel[__DOTCC_PATH_MAX];
    int err;
    int dirfd = __wasi_resolve(linkpath, rel, sizeof rel);
    if (dirfd < 0)
    {
        return -1;
    }
    err = __wasi_path_symlink(target, strlen(target), dirfd, rel, strlen(rel));
    return err ? __wasi_fail(err) : 0;
}
