#include "stdio_impl.h"
#include <wasi.h>

size_t __stdio_read(FILE *f, unsigned char *buf, size_t len)
{
	struct __wasi_iovec iov[2] = {
		{ .buf = __wasi_addr(buf), .buf_len = len - !!f->buf_size },
		{ .buf = __wasi_addr(f->buf), .buf_len = f->buf_size }
	};
	ssize_t cnt;
	unsigned num;

	cnt = __wasi_fd_read(f->fd, iov, 2, &num) ? -1 : (ssize_t)num;
	if (cnt <= 0) {
		f->flags |= cnt ? F_ERR : F_EOF;
		return 0;
	}
	if (cnt <= iov[0].buf_len) return cnt;
	cnt -= iov[0].buf_len;
	f->rpos = f->buf;
	f->rend = f->buf + cnt;
	if (f->buf_size) buf[len-1] = *f->rpos++;
	return len;
}
