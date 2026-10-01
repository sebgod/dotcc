#include "stdio_impl.h"
#include <errno.h>
#include <wasi.h>

int __stdio_close(FILE *f)
{
	int err = __wasi_fd_close(f->fd);
	if (err) {
		errno = __wasi_errno(err);
		return -1;
	}
	return 0;
}
