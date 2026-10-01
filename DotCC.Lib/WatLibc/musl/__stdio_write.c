#include "stdio_impl.h"
#include <wasi.h>

size_t __stdio_write(FILE *f, const unsigned char *buf, size_t len)
{
	struct __wasi_iovec iovs[2] = {
		{ .buf = __wasi_addr(f->wbase), .buf_len = f->wpos-f->wbase },
		{ .buf = __wasi_addr(buf), .buf_len = len }
	};
	struct __wasi_iovec *iov = iovs;
	size_t rem = iov[0].buf_len + iov[1].buf_len;
	int iovcnt = 2;
	ssize_t cnt;
	for (;;) {
		unsigned num;
		cnt = __wasi_fd_write(f->fd, iov, iovcnt, &num) ? -1 : (ssize_t)num;
		if (cnt == rem) {
			f->wend = f->buf + f->buf_size;
			f->wpos = f->wbase = f->buf;
			return len;
		}
		if (cnt < 0) {
			f->wpos = f->wbase = f->wend = 0;
			f->flags |= F_ERR;
			return iovcnt == 2 ? 0 : len-iov[0].buf_len;
		}
		rem -= cnt;
		if (cnt > iov[0].buf_len) {
			cnt -= iov[0].buf_len;
			iov++; iovcnt--;
		}
		iov[0].buf += cnt;
		iov[0].buf_len -= cnt;
	}
}
