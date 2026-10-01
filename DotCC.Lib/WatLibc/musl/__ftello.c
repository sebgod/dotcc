#include "stdio_impl.h"
#include <limits.h>
#include <errno.h>

off_t __ftello(FILE *f)
{
	off_t pos;
	FLOCK(f);
	pos = __ftello_unlocked(f);
	FUNLOCK(f);
	return pos;
}
