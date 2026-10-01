#include "stdio_impl.h"

#undef stdout

/* Unbuffered, as stderr is, where musl's is line-buffered: the wat backend expands a printf
 * with a literal format inline, writing straight to fd 1, and a buffer here would hold back
 * what the program wrote through stdout behind it. */
static unsigned char buf[UNGET];
static FILE __stdout_FILE = {
	.buf = buf+UNGET,
	.buf_size = 0,
	.fd = 1,
	.flags = F_PERM | F_NORD,
	.lbf = -1,
	.write = __stdio_write,
	.seek = __stdio_seek,
	.close = __stdio_close,
	.lock = -1,
};
FILE *const stdout = &__stdout_FILE;
