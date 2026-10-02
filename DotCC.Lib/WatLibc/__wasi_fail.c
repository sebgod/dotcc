/* __wasi_fail: set errno for a WASI errno and return -1, how a POSIX call that failed ends.
   The wat target's libc (WatLibc), compiled with the program. */
#include <errno.h>
#include <posix_impl.h>

int __wasi_fail(int err)
{
    errno = __wasi_errno(err);
    return -1;
}
