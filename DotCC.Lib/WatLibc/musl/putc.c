#include "stdio_impl.h"

int putc(int c, FILE *f)
{
	return putc_unlocked(c, f);
}
