/* __ofl_lock: the list of the streams a program opened, which fflush(NULL) walks; musl's ofl.c,
   less its lock (see stdio_impl.h). The wat target's libc (WatLibc), compiled with the program. */
#include <stdio_impl.h>

static FILE *ofl_head;

FILE **__ofl_lock(void)
{
	return &ofl_head;
}
