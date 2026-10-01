#include "stdio_impl.h"

int getc(FILE *f)
{
	return getc_unlocked(f);
}
