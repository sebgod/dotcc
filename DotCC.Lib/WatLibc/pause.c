/* pause: wait for a signal (POSIX). WASI delivers none, so no wait would end: pause fails
   ENOSYS instead of hanging the program. The wat target's libc (WatLibc), compiled with the program. */
#include <errno.h>
#include <unistd.h>

int pause(void)
{
    errno = ENOSYS;
    return -1;
}
