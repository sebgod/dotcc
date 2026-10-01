/* __uflow: refill a FILE's read buffer. The library's FILEs are so far only the scanning
   helpers' pseudo-FILEs over a string, whose end is the string's NUL, which the scanners stop
   at, so no read reaches here: EOF. The wat target's libc (WatLibc), compiled with the program. */
#include <stdio_impl.h>

int __uflow(FILE *f)
{
	(void)f;
	return EOF;
}
