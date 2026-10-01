#include <stdio.h>
#include <stdarg.h>
#include <limits.h>

int vprintf(const char *restrict fmt, va_list ap)
{
	return vfprintf(stdout, fmt, ap);
}
