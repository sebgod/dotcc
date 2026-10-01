#ifndef _STDIO_IMPL_H
#define _STDIO_IMPL_H

/* musl's src/internal/stdio_impl.h, as far as the wat target's libc needs it yet. A program's
   <stdio.h> keeps FILE opaque, so inside the library FILE is musl's struct _IO_FILE, cut to
   the fields the library reads: the scanning helpers' (shgetc.h), with which strtod and its
   family read a string through a pseudo-FILE. */

#include <stdio.h>
#include <features.h>

struct _IO_FILE {
	unsigned flags;
	unsigned char *rpos, *rend;
	unsigned char *buf;
	unsigned char *shend;
	off_t shlim, shcnt;
};

#define FILE struct _IO_FILE

/* Refill a FILE's read buffer and return its next byte, or EOF. */
hidden int __uflow(FILE *);

#endif
