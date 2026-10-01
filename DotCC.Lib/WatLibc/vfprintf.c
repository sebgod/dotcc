/* vfprintf: format to a stream, in one write. The text is formatted first (vsnprintf), into a
   buffer on the stack or, when it is longer, one from the heap. The wat target's libc (WatLibc), compiled with the program. */
#include <stdio_impl.h>
#include <stdarg.h>
#include <stdlib.h>
#include <errno.h>

int vfprintf(FILE *restrict f, const char *restrict fmt, va_list ap)
{
	char small[256];
	va_list ap2;
	va_copy(ap2, ap);
	int n = vsnprintf(small, sizeof small, fmt, ap2);
	va_end(ap2);
	char *text = small;
	if (n >= (int)sizeof small) {
		text = malloc((size_t)n + 1);
		if (!text) {
			f->flags |= F_ERR;
			errno = ENOMEM;
			return -1;
		}
		vsnprintf(text, (size_t)n + 1, fmt, ap);
	}
	size_t written = fwrite(text, 1, (size_t)n, f);
	if (text != small) free(text);
	return written == (size_t)n ? n : -1;
}
