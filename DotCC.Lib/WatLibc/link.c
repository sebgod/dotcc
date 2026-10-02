/* link: give the file old a second name, new (POSIX), through WASI's path_link; a symbolic link
   old is linked itself, as Linux's link does. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>
#include <unistd.h>
#include <posix_impl.h>

int link(const char *old, const char *new)
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
    err = __wasi_path_link(oldfd, 0, from, strlen(from), newfd, to, strlen(to));
    return err ? __wasi_fail(err) : 0;
}
