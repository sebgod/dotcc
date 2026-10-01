#include "stdio_impl.h"
#include <limits.h>
#include <errno.h>

long ftell(FILE *f)
{
	off_t pos = __ftello(f);
	if (pos > LONG_MAX) {
		errno = EOVERFLOW;
		return -1;
	}
	return pos;
}
