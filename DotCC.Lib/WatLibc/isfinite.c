#include <math.h>

/* A program's <math.h> declares isfinite a function; the library's defines it a macro. */
#undef isfinite
int isfinite(double x)
{
	return (__DOUBLE_BITS(x) & -1ULL>>1) < 0x7ffULL<<52;
}
