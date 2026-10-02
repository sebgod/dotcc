/* utimes: set path's access and modification times, times[0] and times[1], or both to now when
   times is null (POSIX), through WASI's path_filestat_set_times. The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>
#include <sys/time.h>
#include <posix_impl.h>

int utimes(const char *path, const struct timeval times[2])
{
    char rel[__DOTCC_PATH_MAX];
    unsigned long atim = 0;
    unsigned long mtim = 0;
    unsigned flags = __WASI_FSTFLAGS_ATIM_NOW | __WASI_FSTFLAGS_MTIM_NOW;
    int err;
    int dirfd = __wasi_resolve(path, rel, sizeof rel);
    if (dirfd < 0)
    {
        return -1;
    }
    if (times)
    {
        atim = (unsigned long)times[0].tv_sec * 1000000000UL + (unsigned long)times[0].tv_usec * 1000UL;
        mtim = (unsigned long)times[1].tv_sec * 1000000000UL + (unsigned long)times[1].tv_usec * 1000UL;
        flags = __WASI_FSTFLAGS_ATIM | __WASI_FSTFLAGS_MTIM;
    }
    err = __wasi_path_filestat_set_times(dirfd, __WASI_LOOKUP_SYMLINK_FOLLOW, rel, strlen(rel), atim, mtim, flags);
    return err ? __wasi_fail(err) : 0;
}
