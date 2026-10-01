#include "shgetc.h"

/* The shcnt field stores the number of bytes read so far, offset by
 * the value of buf-rpos at the last function call (__shlim or __shgetc),
 * so that between calls the inline shcnt macro can add rpos-buf to get
 * the actual count. */

void __shlim(FILE *f, off_t lim)
{
	f->shlim = lim;
	f->shcnt = f->buf - f->rpos;
	/* If lim is nonzero, rend must be a valid pointer. */
	if (lim && f->rend - f->rpos > lim)
		f->shend = f->rpos + lim;
	else
		f->shend = f->rend;
}
