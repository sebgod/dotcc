/* rename: give the file old the name new (C11 7.21.4.2, as POSIX has it), through WASI's
   path_rename. The wat target's libc (WatLibc), compiled with the program. */
#include <stdio.h>
#include <string.h>
#include <posix_impl.h>

int rename(const char *old, const char *new)
{
    char from[__DOTCC_PATH_MAX];
    char to[__DOTCC_PATH_MAX];
    int err;
    int oldfd = __wasi_resolve(old, from, sizeof from);
    int newfd = oldfd < 0 ? -1 : __wasi_resolve(new, to, sizeof to);
    if (newfd < 0)
    {
        return -1;
    }
    err = __wasi_path_rename(oldfd, from, strlen(from), newfd, to, strlen(to));
    return err ? __wasi_fail(err) : 0;
}
