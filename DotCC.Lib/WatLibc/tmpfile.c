/* tmpfile: an anonymous file, open for update, gone when closed. WASI preview1 has no anonymous
   files, and a tmpfile is gone when the program ends anyway, so this one lives in memory: musl's
   fmemopen stream (src/stdio/fmemopen.c) over a buffer that grows as it is written, reading back
   zeros where a write past the end left a gap, as a file does. The wat target's libc (WatLibc), compiled with the program. */
#include <stdio_impl.h>
#include <errno.h>
#include <string.h>
#include <stdlib.h>

struct cookie {
	size_t pos, len, size;
	unsigned char *buf;
};

struct mem_FILE {
	FILE f;
	struct cookie c;
	unsigned char buf[UNGET+BUFSIZ];
};

static off_t mseek(FILE *f, off_t off, int whence)
{
	struct cookie *c = f->cookie;
	off_t base;
	if (whence == SEEK_SET) base = 0;
	else if (whence == SEEK_CUR) base = c->pos;
	else if (whence == SEEK_END) base = c->len;
	else {
		errno = EINVAL;
		return -1;
	}
	if (off < -base) {
		errno = EINVAL;
		return -1;
	}
	return c->pos = base+off;
}

static size_t mread(FILE *f, unsigned char *buf, size_t len)
{
	struct cookie *c = f->cookie;
	size_t rem = c->len - c->pos;
	if (c->pos > c->len) rem = 0;
	if (len > rem) {
		len = rem;
		f->flags |= F_EOF;
	}
	memcpy(buf, c->buf+c->pos, len);
	c->pos += len;
	rem -= len;
	if (rem > f->buf_size) rem = f->buf_size;
	f->rpos = f->buf;
	f->rend = f->buf + rem;
	memcpy(f->rpos, c->buf+c->pos, rem);
	c->pos += rem;
	return len;
}

static size_t mwrite(FILE *f, const unsigned char *buf, size_t len)
{
	struct cookie *c = f->cookie;
	size_t len2 = f->wpos - f->wbase;
	if (len2) {
		f->wpos = f->wbase;
		if (mwrite(f, f->wpos, len2) < len2) return 0;
	}
	if (c->pos + len > c->size) {
		size_t size = c->size ? c->size : BUFSIZ;
		while (size < c->pos + len) size *= 2;
		unsigned char *grown = realloc(c->buf, size);
		if (!grown) {
			f->flags |= F_ERR;
			return 0;
		}
		c->buf = grown;
		c->size = size;
	}
	if (c->pos > c->len) memset(c->buf+c->len, 0, c->pos-c->len);
	memcpy(c->buf+c->pos, buf, len);
	c->pos += len;
	if (c->pos > c->len) c->len = c->pos;
	return len;
}

static int mclose(FILE *f)
{
	struct cookie *c = f->cookie;
	free(c->buf);
	return 0;
}

FILE *tmpfile(void)
{
	struct mem_FILE *f = calloc(1, sizeof *f);
	if (!f) return 0;
	f->f.cookie = &f->c;
	f->f.fd = -1;
	f->f.lbf = EOF;
	f->f.buf = f->buf + UNGET;
	f->f.buf_size = sizeof f->buf - UNGET;
	f->f.read = mread;
	f->f.write = mwrite;
	f->f.seek = mseek;
	f->f.close = mclose;
	f->f.lock = -1;
	return __ofl_add(&f->f);
}
