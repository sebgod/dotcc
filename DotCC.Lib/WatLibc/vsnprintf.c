/* vsnprintf: format into s, at most n bytes with the terminating NUL, and return the length of the
   whole output. The format is read here, at run time; each conversion is laid out by the wat
   backend's formatter, the runtime the inline expansion of a literal format calls, so the two
   print alike (printf_impl.h). long double is double on this target; %lc and %ls are a wide
   character and string, written as UTF-8. The wat target's libc (WatLibc), compiled with the program. */
#include <stdio.h>
#include <stddef.h>
#include <stdarg.h>
#include <printf_impl.h>
#include <stdlib.h>
#include <wide_impl.h>

/* Lay out the k bytes at bytes in a field width wide, spaces before them or (left) after. */
static void pad_wide(const char *bytes, int k, int width, int left)
{
	for (int i = k; !left && i < width; i++) __builtin_dotcc_pf_write(" ", 1);
	__builtin_dotcc_pf_write(bytes, k);
	for (int i = k; left && i < width; i++) __builtin_dotcc_pf_write(" ", 1);
}

int vsnprintf(char *restrict s, size_t n, const char *restrict fmt, va_list ap)
{
	/* The formatter's bound is a signed 32-bit address. */
	char *end = n > 0x7fffffffUL - (unsigned long)s ? (char *)0x7fffffffUL : s + n;
	__builtin_dotcc_sink(s, end);
	const char *p = fmt;
	while (*p) {
		if (*p != '%') {
			const char *q = p;
			while (*q && *q != '%') q++;
			__builtin_dotcc_pf_write(p, (int)(q - p));
			p = q;
			continue;
		}
		const char *spec = p++;
		int left = 0, plus = 0, space = 0, alt = 0, zero = 0;
		for (;; p++) {
			if (*p == '-') left = 1;
			else if (*p == '+') plus = 1;
			else if (*p == ' ') space = 1;
			else if (*p == '#') alt = 1;
			else if (*p == '0') zero = 1;
			else break;
		}
		int width = 0;
		if (*p == '*') {
			width = va_arg(ap, int);
			if (width < 0) {
				left = 1;
				width = -width;
			}
			p++;
		} else {
			while (*p >= '0' && *p <= '9') width = width * 10 + (*p++ - '0');
		}
		int prec = -1;
		if (*p == '.') {
			p++;
			if (*p == '*') {
				prec = va_arg(ap, int);
				if (prec < 0) prec = -1;
				p++;
			} else {
				prec = 0;
				while (*p >= '0' && *p <= '9') prec = prec * 10 + (*p++ - '0');
			}
		}
		/* The argument's width: 'H' for hh, 'h', 'l' for every 64-bit one, 'L'. */
		int len = 0;
		switch (*p) {
		case 'h':
			p++;
			if (*p == 'h') {
				p++;
				len = 'H';
			} else {
				len = 'h';
			}
			break;
		case 'l':
			p++;
			if (*p == 'l') p++;
			len = 'l';
			break;
		case 'j': case 'z': case 't':
			p++;
			len = 'l';
			break;
		case 'L':
			p++;
			len = 'L';
			break;
		}
		int conv = *p;
		if (conv) p++;
		int sign = plus ? '+' : space ? ' ' : 0;
		/* The fill: 1 left-justifies, 2 pads with zeros, 0 with spaces. A precision turns an
		   integer's zero padding off; a float's it does not. */
		int imode = left ? 1 : zero && prec < 0 ? 2 : 0;
		int fmode = left ? 1 : zero ? 2 : 0;
		int min = prec >= 0 ? prec : 1;
		if (min > __PF_MAX_DIGITS) min = __PF_MAX_DIGITS;
		switch (conv) {
		case 'd': case 'i': {
			long v = len == 'l' ? va_arg(ap, long) : va_arg(ap, int);
			if (len == 'h') v = (short)v;
			else if (len == 'H') v = (signed char)v;
			__builtin_dotcc_pf_int(v, sign, min, width, imode);
			break;
		}
		case 'u': case 'o': case 'x': case 'X': {
			unsigned long v = len == 'l' ? va_arg(ap, unsigned long) : va_arg(ap, unsigned);
			if (len == 'h') v = (unsigned short)v;
			else if (len == 'H') v = (unsigned char)v;
			int base = conv == 'u' ? 10 : conv == 'o' ? 8 : 16;
			int alpha = conv == 'x' ? 'a' : conv == 'X' ? 'A' : 0;
			int prefix = alt && conv == 'x' ? '0' | 'x' << 8 : alt && conv == 'X' ? '0' | 'X' << 8 : 0;
			__builtin_dotcc_pf_uint(v, base, alpha, min, width, imode, prefix, alt && conv == 'o');
			break;
		}
		case 'c':
			if (len == 'l') {
				/* A wide character, as its UTF-8 bytes. */
				wchar_t one[2] = { (wchar_t)va_arg(ap, int), 0 };
				char bytes[4];
				size_t k = one[0] ? __wcs_to_utf8(bytes, one, sizeof bytes) : 1;
				if (!one[0]) bytes[0] = 0;
				pad_wide(bytes, (int)k, width, left);
			} else {
				__builtin_dotcc_pf_char(va_arg(ap, int), width, left);
			}
			break;
		case 's':
			if (len == 'l') {
				/* A wide string, as UTF-8: the precision bounds its bytes, whole characters
				   only. */
				const wchar_t *ws = va_arg(ap, const wchar_t *);
				size_t max = prec >= 0 ? (size_t)prec : (size_t)-1;
				size_t k = __wcs_to_utf8(NULL, ws, max);
				char small[256];
				char *bytes = k <= sizeof small ? small : malloc(k);
				if (bytes) {
					__wcs_to_utf8(bytes, ws, k);
					pad_wide(bytes, (int)k, width, left);
					if (bytes != small) free(bytes);
				}
			} else {
				__builtin_dotcc_pf_str(va_arg(ap, const char *), prec, width, left);
			}
			break;
		case 'p':
			__builtin_dotcc_pf_p(va_arg(ap, void *), width, left);
			break;
		case 'f': case 'F': case 'e': case 'E': case 'g': case 'G': case 'a': case 'A': {
			double v = va_arg(ap, double);
			int lower = conv | 32;
			int upper = conv != lower;
			int fprec = prec >= 0 ? prec : lower == 'a' ? -1 : 6;
			if (lower == 'g' && fprec == 0) fprec = 1;
			if (fprec > __PF_MAX_PREC) fprec = __PF_MAX_PREC;
			if (lower == 'f') __builtin_dotcc_pf_f(v, fprec, sign, width, fmode, alt, upper);
			else if (lower == 'e') __builtin_dotcc_pf_e(v, fprec, sign, width, fmode, alt, upper);
			else if (lower == 'g') __builtin_dotcc_pf_g(v, fprec, sign, width, fmode, alt, upper);
			else __builtin_dotcc_pf_a(v, fprec, sign, width, fmode, alt, upper);
			break;
		}
		case 'n': {
			int count = __builtin_dotcc_sink_count();
			if (len == 'l') *va_arg(ap, long *) = count;
			else if (len == 'h') *va_arg(ap, short *) = (short)count;
			else if (len == 'H') *va_arg(ap, signed char *) = (signed char)count;
			else *va_arg(ap, int *) = count;
			break;
		}
		case '%':
			__builtin_dotcc_pf_write("%", 1);
			break;
		default:
			/* Not a conversion: printed as it stands. */
			__builtin_dotcc_pf_write(spec, (int)(p - spec));
			break;
		}
	}
	return __builtin_dotcc_sink_end();
}
