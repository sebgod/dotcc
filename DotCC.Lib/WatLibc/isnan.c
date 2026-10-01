#include <math.h>

/* A program's <math.h> declares isnan a function; the library's defines it a macro. */
#undef isnan
int isnan(double x)
{
	return (__DOUBLE_BITS(x) & -1ULL>>1) > 0x7ffULL<<52;
}
