#include "stdio_impl.h"
#include <errno.h>

int fseek(FILE *f, long off, int whence)
{
	return __fseeko(f, off, whence);
}
