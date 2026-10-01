#include <stdio.h>
#include <scanf_impl.h>
#include <stdarg.h>

int vscanf(const char *restrict fmt, va_list ap)
{
	return vfscanf(stdin, fmt, ap);
}
