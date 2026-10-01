#include "stdio_impl.h"
#include <errno.h>
#include <wasi.h>

off_t __stdio_seek(FILE *f, off_t off, int whence)
{
	unsigned long pos;
	int err = __wasi_fd_seek(f->fd, off, whence, &pos);
	if (err) {
		errno = __wasi_errno(err);
		return -1;
	}
	return pos;
}
