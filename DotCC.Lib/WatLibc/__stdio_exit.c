/* __stdio_exit: flush every stream at exit, musl's name for it; exit calls it through
   __dotcc_flush_at_exit, which the stdio layer points here once a stream that can hold output
   back exists (__ofl_lock, setvbuf). The wat target's libc (WatLibc), compiled with the program. */
#include <stdio_impl.h>

void __stdio_exit(void)
{
	fflush(0);
}
