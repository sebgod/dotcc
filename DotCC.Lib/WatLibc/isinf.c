#include <math.h>

/* A program's <math.h> declares isinf a function; the library's defines it a macro. */
#undef isinf
int isinf(double x)
{
	return (__DOUBLE_BITS(x) & -1ULL>>1) == 0x7ffULL<<52;
}
