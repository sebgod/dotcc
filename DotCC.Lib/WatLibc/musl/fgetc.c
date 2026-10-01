#include "stdio_impl.h"

int fgetc(FILE *f)
{
	return getc_unlocked(f);
}
