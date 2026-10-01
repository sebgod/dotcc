#include "stdio_impl.h"

int fputc(int c, FILE *f)
{
	return putc_unlocked(c, f);
}
