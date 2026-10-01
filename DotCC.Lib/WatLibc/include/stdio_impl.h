#ifndef _STDIO_IMPL_H
#define _STDIO_IMPL_H

/* musl's src/internal/stdio_impl.h, for the wat target's libc. A program's <stdio.h> keeps FILE
   opaque (dotcc's predefined type name), so inside the library FILE is musl's struct _IO_FILE,
   defined before anything here names it. Two departures from musl:
   - stdio takes no locks yet: FLOCK and FUNLOCK are empty and a FILE's lock stays -1, musl's own
     state for a program with one thread. A threaded program must not use one stream from two
     threads at once.
   - nothing is weak (dotcc has no weak symbols), so what musl reaches through a weak alias
     (__stdout_used and the like) is reached directly, and the hooks that exist only to pull
     __stdio_exit in are gone; the standard streams are unbuffered (see stdout.c), so there is
     nothing to flush at exit yet. */

#include <stdio.h>
#include <stdarg.h>
#include <stddef.h>
#include <sys/types.h>
#include <features.h>

#define FILE struct _IO_FILE

#define UNGET 8

#define FFINALLOCK(f) ((void)0)
#define FLOCK(f) ((void)0)
#define FUNLOCK(f) ((void)0)

#define F_PERM 1
#define F_NORD 4
#define F_NOWR 8
#define F_EOF 16
#define F_ERR 32
#define F_SVB 64
#define F_APP 128

struct _IO_FILE {
	unsigned flags;
	unsigned char *rpos, *rend;
	int (*close)(FILE *);
	unsigned char *wend, *wpos;
	unsigned char *mustbezero_1;
	unsigned char *wbase;
	size_t (*read)(FILE *, unsigned char *, size_t);
	size_t (*write)(FILE *, const unsigned char *, size_t);
	off_t (*seek)(FILE *, off_t, int);
	unsigned char *buf;
	size_t buf_size;
	FILE *prev, *next;
	int fd;
	int pipe_pid;
	long lockcount;
	int mode;
	volatile int lock;
	int lbf;
	void *cookie;
	off_t off;
	char *getln_buf;
	void *mustbezero_2;
	unsigned char *shend;
	off_t shlim, shcnt;
	FILE *prev_locked, *next_locked;
	struct __locale_struct *locale;
};

hidden size_t __stdio_read(FILE *, unsigned char *, size_t);
hidden size_t __stdio_write(FILE *, const unsigned char *, size_t);
hidden off_t __stdio_seek(FILE *, off_t, int);
hidden int __stdio_close(FILE *);

hidden int __toread(FILE *);
hidden int __towrite(FILE *);

int __overflow(FILE *, int);
int __uflow(FILE *);

hidden int __fseeko(FILE *, off_t, int);
hidden int __fseeko_unlocked(FILE *, off_t, int);
hidden off_t __ftello(FILE *);
hidden off_t __ftello_unlocked(FILE *);
hidden size_t __fwritex(const unsigned char *, size_t, FILE *);

hidden FILE *__ofl_add(FILE *f);
hidden FILE **__ofl_lock(void);
hidden void __ofl_unlock(void);

#define feof(f) ((f)->flags & F_EOF)
#define ferror(f) ((f)->flags & F_ERR)

#define getc_unlocked(f) \
	( ((f)->rpos != (f)->rend) ? *(f)->rpos++ : __uflow((f)) )

#define putc_unlocked(c, f) \
	( (((unsigned char)(c)!=(f)->lbf && (f)->wpos!=(f)->wend)) \
	? *(f)->wpos++ = (unsigned char)(c) \
	: __overflow((f),(unsigned char)(c)) )

#endif
