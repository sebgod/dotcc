/* usleep: block for usec microseconds (POSIX). The wat target's libc (WatLibc), compiled with the program. */
#include <unistd.h>
#include <posix_impl.h>

int usleep(unsigned int usec)
{
    int err = __wasi_sleep((unsigned long)usec * 1000UL);
    return err ? __wasi_fail(err) : 0;
}
