/* sleep: block for seconds seconds (POSIX); no signal cuts it short, so what is left is 0.
   The wat target's libc (WatLibc), compiled with the program. */
#include <unistd.h>
#include <posix_impl.h>

unsigned int sleep(unsigned int seconds)
{
    __wasi_sleep((unsigned long)seconds * 1000000000UL);
    return 0;
}
