#include "stdio_impl.h"

int getchar(void)
{
	return getc_unlocked(stdin);
}
