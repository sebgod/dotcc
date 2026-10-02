/* rmdir: remove the empty directory path (POSIX), through WASI's path_remove_directory. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>
#include <unistd.h>
#include <posix_impl.h>

int rmdir(const char *path)
{
    char rel[__DOTCC_PATH_MAX];
    int err;
    int dirfd = __wasi_resolve(path, rel, sizeof rel);
    if (dirfd < 0)
    {
        return -1;
    }
    err = __wasi_path_remove_directory(dirfd, rel, strlen(rel));
    return err ? __wasi_fail(err) : 0;
}
