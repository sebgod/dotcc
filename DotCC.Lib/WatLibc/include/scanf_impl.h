#ifndef _SCANF_IMPL_H
#define _SCANF_IMPL_H

/* The scanf family's va_list forms, which dotcc's <stdio.h> does not declare (the C# runtime
   has none): the wat libc's scanf, fscanf, sscanf and vscanf call them. */

#include <stdio.h>

int vfscanf(FILE *restrict f, const char *restrict fmt, VaList ap);
int vsscanf(const char *restrict s, const char *restrict fmt, VaList ap);
int vscanf(const char *restrict fmt, VaList ap);

#endif
