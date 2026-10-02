/* mkdir: make directory path (POSIX), through WASI's path_create_directory; WASI has no permissions, so mode is not asked for. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>
#include <sys/stat.h>
#include <posix_impl.h>

int mkdir(const char *path, unsigned int mode)
{
    char rel[__DOTCC_PATH_MAX];
    int err;
    int dirfd = __wasi_resolve(path, rel, sizeof rel);
    if (dirfd < 0)
    {
        return -1;
    }
    err = __wasi_path_create_directory(dirfd, rel, strlen(rel));
    return err ? __wasi_fail(err) : 0;
}
